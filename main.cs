using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using KSP.UI.Screens.Flight;
using System.Runtime.InteropServices.WindowsRuntime;
using Expansions.Missions.Tests;
using LibNoise.Models;
using Smooth.Compare;
using System.Diagnostics.Eventing.Reader;
using static VehiclePhysics.ProjectPatchAsset;

#region Silencers
#pragma warning disable IDE0044
#pragma warning disable IDE0060
#pragma warning disable IDE1006
#endregion

namespace kLeapDrive
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class kFTLCore : PartModule
    {
        #region Variables
        private int propellantID;
        private bool disengageAtTgt = false;
        private sbyte flightInfoStatus = -1; //-1: Not Supercruising, 0: Supercruising, 1: Ready to drop out at target
        private string formattedVelString;
        private SpeedDisplay speedDisplay = null;
        private Color defaultTitleColor;
        private Dictionary<CelestialBody, double> cbDistsToWarpLoc = new Dictionary<CelestialBody, double>();
        #endregion

        #region Constants
        private readonly static int c = 299_792_458;
        private readonly static double ly = 9.461e+15;
        private readonly static float heightAboveMin = 10_000.0f;
        private readonly static float destinationLockRange = 300_000.0f; //ED Lock-Range is ~1Mm (1_000_000), default reduced to 300km for scale reasons
        private readonly static float rendezvousDistance = 8_000.0f;
        private readonly static float limiterFactor = 0.001f;
        private readonly static float accelerationRate = 0.5f;
        private readonly static float[] speedRange = { 30000.0f, 2.0f * c }; //Max out at 2c, 2001c (speed limit in ED) is too high for KSP to handle | Just keeping this in case I need it later:ED acceleration rate is ~0.6c/s (57 minutes to reach max speed of 2001c)
        private readonly static Color alertColor = new Color(1.0f, 0.65f, 0.0f);
        #endregion

        #region KSPFields
        [KSPField]
        public double MassLimit = double.MaxValue;
        [KSPField]
        public double MinimumJumpTargetMass = 0.0d; //Asteroid moons shouldn't be able to be jumped to, as their gravity is too weak
        [KSPField]
        public double SCFuelRate = 0.0d; //Consume a flat amount of fuel no matter the speed, in Elite Dangerous this rate is determined by the powerplant specifics, which is not modeled here.
        [KSPField]
        public double FuelPerLY = 0.0d;
        [KSPField]
        public string FuelResource = "LiquidFuel";
        [KSPField]
        public bool AllowNonStellarTargets = true; //Makes drive act like Capital Ship FSDs in ED, allowing you to jump to any target
        [KSPField(isPersistant = true)]
        protected bool vesselIsSupercruising = false;
        [KSPField(isPersistant = true)]
        protected float currentVel = 30000.0f;
        [KSPField(isPersistant = true)]
        protected float desiredVel = 30000.0f;
        [KSPField(isPersistant = true)]
        protected float limitVel = 30000.0f;
        #endregion

        public void OnGUI()
        {
            if (speedDisplay == null)
            {
                try
                {
                    speedDisplay = GameObject.FindObjectOfType<SpeedDisplay>();
                    defaultTitleColor = speedDisplay.textTitle.color;
                }
                catch { }
            }
        }

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            //Store ID to avoid calling GetDefinition every time
            propellantID = PartResourceLibrary.Instance.GetDefinition(FuelResource).id;

        }

        #region Action Groups
        [KSPAction(guiName = "Toggle Supercruise")]
        public void ToggleSupercruiseAction(KSPActionParam param)
        {
            ToggleSupercruise();
        }

        [KSPAction(guiName = "Perform Hyperspace Jump")]
        public void JumpAction(KSPActionParam param)
        {
            CommenceJumpSequence();
        }
        #endregion

        [KSPEvent(guiActive = true, active = true, guiActiveEditor = false, guiName = "Toggle Supercruise", guiActiveUnfocused = false, isPersistent = false)]
        public void ToggleSupercruise()
        {   
            if (part.vessel.GetTotalMass() > MassLimit)
            {
                ScreenMessages.PostScreenMessage("Vessel exceeds mass limit, cannot engage!", 3.0f, ScreenMessageStyle.UPPER_CENTER, alertColor);
                vesselIsSupercruising = false;
                return;
            }
            if (!(part.vessel.altitude < (part.vessel.mainBody.minOrbitalDistance - part.vessel.mainBody.Radius + heightAboveMin)))
            {
                /*foreach (Part part in part.vessel.parts)
                {
                    var core = part.FindModuleImplementing<kFTLCore>();
                    if (core.vesselIsSupercruising  && core != this) return;
                }*/
                foreach (kFTLCore core in part.vessel.FindPartModulesImplementing<kFTLCore>())
                {
                    if (core.vesselIsSupercruising && core != this) return;
                }
                currentVel = speedRange[0];
                vesselIsSupercruising = !vesselIsSupercruising;
                if (!vesselIsSupercruising) SetSCState(false);
                else SetSCState(true);
            }
        }

        //The Heart of Supercruise
        public void FixedUpdate()
        {
            if (vesselIsSupercruising)
            {
                if (part.vessel != FlightGlobals.ActiveVessel) SetSCState(false);
                if (!ConsumeResource(propellantID, SCFuelRate * Time.fixedDeltaTime))
                {
                    SetSCState(false);
                    ScreenMessages.PostScreenMessage("Emergency Drop: No Fuel", 3.0f, ScreenMessageStyle.UPPER_CENTER, alertColor);
                    return;
                }
                float throttleLevel = part.vessel.ctrlState.mainThrottle;
                if ((part.vessel.altitude < part.vessel.mainBody.minOrbitalDistance - part.vessel.mainBody.Radius + heightAboveMin))
                {
                    //I don't think this ever gets triggered
                    SetSCState(false);
                    ScreenMessages.PostScreenMessage("Emergency Drop: Too Close", 3.0f, ScreenMessageStyle.UPPER_CENTER, alertColor);
                    return;
                }
                else
                {
                    limitVel = GetLimitVelocity();
                    desiredVel = Mathf.Clamp(throttleLevel * limitVel, speedRange[0], speedRange[1]);
                    currentVel = Mathf.Clamp(Mathf.Lerp(currentVel, desiredVel, accelerationRate * Time.fixedDeltaTime), speedRange[0], limitVel);
                    Vector3d translatedVector = part.vessel.GetWorldPos3D() + part.vessel.transform.up.normalized * currentVel * Time.fixedDeltaTime;
                    cbDistsToWarpLoc.Clear();
                    //This operation might add a bit of lag, but you dont want people to warp into bodies
                    foreach (CelestialBody b in FlightGlobals.Bodies)
                    {
                        cbDistsToWarpLoc.Add(b, (translatedVector - b.position).sqrMagnitude);
                    }
                    CelestialBody closestBody = cbDistsToWarpLoc.Aggregate((l, r) => l.Value < r.Value ? l : r).Key;
                    if (cbDistsToWarpLoc[closestBody] > Math.Pow(closestBody.minOrbitalDistance + heightAboveMin, 2))
                    {
                        if (FlightGlobals.VesselsLoaded.Count > 1) part.vessel.SetPosition(translatedVector);
                        else FloatingOrigin.SetOutOfFrameOffset(translatedVector);
                    }
                    else
                    {
                        SetSCState(false);
                        ScreenMessages.PostScreenMessage("Emergency Drop: Impact Imminent", 3.0f, ScreenMessageStyle.UPPER_CENTER, alertColor);
                        return;
                    }
                    disengageAtTgt = (part.vessel.targetObject != null) ? RendezvousCheck() : disengageAtTgt = false;
                    if (disengageAtTgt) flightInfoStatus = 1; else flightInfoStatus = 0;
                    if (!PauseMenu.isOpen)
                    {
                        FlightInputHandler.fetch.stageLock = true;
                        TimeWarp.SetRate(0, true, postScreenMessage: false);
                    }
                    SetSpeedDisplay();
                }
            }
        }

        //Navball Updates
        public void LateUpdate()
        {
            if (vesselIsSupercruising)
            {
                SetSpeedDisplay();
            }
        }

        //Format our velocity and display it
        public void SetSpeedDisplay()
        {
            if (currentVel < 1000000) formattedVelString = (currentVel / 1000).ToString("F1") + "km/s";
            else if (currentVel < 0.1 * c) formattedVelString = (currentVel / 1000000).ToString("F2") + "Mm/s";
            else formattedVelString = (currentVel / c).ToString("F2") + "c";
            switch (flightInfoStatus)
            {
                case 0:
                    speedDisplay.textTitle.text = "Cruise Velocity:";
                    speedDisplay.textTitle.color = defaultTitleColor;
                    break;
                case 1:
                    speedDisplay.textTitle.text = "[Disengage]";
                    speedDisplay.textTitle.color = Color.cyan;
                    break;
                default:
                    speedDisplay.textTitle.text = "Cruise Velocity:";
                    speedDisplay.textTitle.color = defaultTitleColor;
                    break;
            }
            speedDisplay.textSpeed.text = formattedVelString;
            //Is this overcomplicated?
        }

        //Determine the highest possible velocity in the current scenario based on distance to celestial bodies.
        public float GetLimitVelocity()
        {
            var relativeAccelerationList = new List<double>();
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (!body.isStar)
                {
                    double relAcc = body.gravParameter / (part.vessel.GetWorldPos3D() - body.position).sqrMagnitude;
                    //Reject too small influences
                    if (relAcc > 1e-8) { relativeAccelerationList.Add(Math.Sqrt(relAcc)); }
                }
            }
            float x = (float) relativeAccelerationList.Sum();
            float factor = 1.0f - (float) ((1 + limiterFactor) * x / (x + limiterFactor));
            return Mathf.Clamp(speedRange[1] * factor, speedRange[0], speedRange[1]);
        }

        //Switch between Supercruise States
        public void SetSCState(bool state)
        {
            vesselIsSupercruising = state;
            if (!state)
            {
                part.vessel.IgnoreGForces(3);
                flightInfoStatus = -1;
                if (disengageAtTgt)
                {
                    FlightGlobals.fetch.SetShipOrbitRendezvous(part.vessel.targetObject.GetVessel(), UnityEngine.Random.onUnitSphere * rendezvousDistance, Vector3d.zero);
                }
                else OrbitMagic();
                //Sometimes this breaks, not sure why
                FlightGlobals.SetSpeedMode(FlightGlobals.SpeedDisplayModes.Orbit);
                speedDisplay.textTitle.color = defaultTitleColor;
            }
            else flightInfoStatus = 0;
        }

        //Determine if you can "lock" and rendezvous with the selected target
        public bool RendezvousCheck()
        {
            Vessel lockedTgt = part.vessel.targetObject?.GetVessel();
            if (lockedTgt != null)
            {         
                Vector3d tgtRelPos = part.vessel.GetWorldPos3D() - lockedTgt.GetWorldPos3D();
                if (tgtRelPos.sqrMagnitude < (destinationLockRange * destinationLockRange) && lockedTgt.mainBody == part.vessel.mainBody)
                {
                    if (!LineAndSphereIntersects(part.vessel.GetWorldPos3D(), lockedTgt.GetWorldPos3D(), part.vessel.mainBody.position, part.vessel.mainBody.minOrbitalDistance))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        //Does some Dark Magic to determine the Orbit based on the Vessels position and orientation relative to the Planet.
        public void OrbitMagic()
        {
            Orbit currentOrbit = part.vessel.GetCurrentOrbit();
            double currentTime = Planetarium.GetUniversalTime();
            currentOrbit = new Orbit(0.0d, 0.0d, part.vessel.altitude + part.vessel.mainBody.Radius, currentOrbit.LAN, currentOrbit.argumentOfPeriapsis, currentOrbit.meanAnomalyAtEpoch, currentTime, part.vessel.mainBody);
            Vector3d normal = part.vessel.GetWorldPos3D() - part.vessel.mainBody.position;
            Vector3d offset = part.vessel.transform.up * 1_00f;
            //The Math here turned out quite simple actually, I got a sheet of paper full of the calculations to get to this point
            double lambda = Vector3d.Dot(offset, normal) / normal.sqrMagnitude;
            Vector3d tangentialVector = offset - lambda * normal;
            Vector3d tangentialVelocity = tangentialVector.normalized * currentOrbit.getOrbitalSpeedAt(currentTime);
            part.vessel.ChangeWorldVelocity(tangentialVelocity - part.vessel.GetObtVelocity());
        }

        //hop hop
        [KSPEvent(guiActive = true, active = true, guiActiveEditor = false, guiName = "Perform Hyperspace Jump", guiActiveUnfocused = false)]
        public void CommenceJumpSequence()
        {
            CelestialBody targetDestination = part.vessel.patchedConicSolver.targetBody;
            //Vector3 diff = currentVessel.patchedConicSolver.targetBody.GetTransform().position - currentVessel.GetTransform().position;
            if (targetDestination != null && targetDestination != part.vessel.mainBody && part.vessel.altitude > (part.vessel.mainBody.minOrbitalDistance - part.vessel.mainBody.Radius))
            {
                double jumpDistance = (part.vessel.GetWorldPos3D() - targetDestination.position).magnitude;
                ConsumeResource(propellantID, (jumpDistance / ly) * FuelPerLY);
                HyperspaceJump(targetDestination);
            }
        }

        //Actually jumps the ship
        public void HyperspaceJump(CelestialBody target)
        {
            vesselIsSupercruising = false;
            OrbitPhysicsManager.HoldVesselUnpack(1);
            FlightGlobals.fetch.SetShipOrbit(target.flightGlobalsIndex, 0, target.minOrbitalDistance * 2, 0, 0, 0, 0, 0);
            //ED points you at the star, but I'm feeling nice :)
            Vector3d radial = part.vessel.GetWorldPos3D() - part.vessel.mainBody.position;
            part.vessel.SetRotation(Quaternion.LookRotation(RandomOrthoVector(radial), radial)); //Points outwards for some reason, but that's what we want, so...
            //Post-Jump dethrottle so you don't go where you don't want to go (like into a star)
            currentVel = speedRange[0];
            SetSCState(true);
        }

        //Consumes specified amount of fuel (returns false if there was not enough fuel, true if, well, true)
        public bool ConsumeResource(int resourceID, double amount, bool checkBeforeRequest = false)
        {
            if (CheatOptions.InfinitePropellant) return true;
            //checkBeforeRequest to true when consuming lots of fuel (jumping)
            if (checkBeforeRequest)
            {
                //We don't want to drain fuel if there's not enough of it, so we check before requesting
                part.GetConnectedResourceTotals(resourceID, out double available, out double _);
                if (available < amount) return false;
                part.RequestResource(resourceID, amount);
                return true;
                
            }
            float received = (float) part.RequestResource(resourceID, amount);
            return Mathf.Approximately(received, (float)amount);
        }

        #region Static Utility Functions
        //Checks if a direction vector is pointing "close" to another direction vector
        //v1 and v2 need to originate from the same point so that Vector3.Angle does not fuck up
        public static bool NearCollinearCheck(Vector3 v1, Vector3 v2, double threshold)
        {
            if (Vector3.Angle(v1, v2) < threshold) return true; else return false;
        }

        //Check if a line segment intersects with a Sphere
        public static bool LineAndSphereIntersects(Vector3d p1, Vector3d p2, Vector3d center, double radius)
        {
            double sqrRadius = radius * radius;
            Vector3d a = p1 - center;
            Vector3d b = p2 - center;
            Vector3d c = p2 - p1;
            double sqrHeight;
            //If height is outside the triangle between p1, p2 and the center, set it to the length of the corresponding edge
            if (180.0d - Vector3d.Angle(a, c) >= 90.0d) sqrHeight = a.sqrMagnitude;
            else if (180.0d - Vector3d.Angle(b, c) >= 90.0d) sqrHeight = b.sqrMagnitude;
            else sqrHeight = Math.Abs(Vector3d.Cross(a, b).sqrMagnitude / c.sqrMagnitude);
            if (sqrHeight < sqrRadius) return true;
            else return false;
        }

        //Generate a vector orthagonal to the input, with a random rotation around it
        public static Vector3 RandomOrthoVector(Vector3 input)
        {
            Vector3 output;
            //Must avoid rare cases where vectors are parallel
            do { output = UnityEngine.Random.onUnitSphere; }
            while (Vector3.Dot(input, output) == input.magnitude * output.magnitude);
            Vector3.OrthoNormalize(ref input, ref output);
            return output;
        }
        #endregion
    }
    public class kFTLGenerator : PartModule
    {

    }
}



/*if (NearCollinearCheck(currentVessel.GetTransform().forward, diff, 5))
{
    if (currentVessel.ctrlState.mainThrottle == 1.0f)
    {
        HyperspaceJump(targetDestination);
    }
    else ScreenMessages.PostScreenMessage("Throttle up to engage");
}
else ScreenMessages.PostScreenMessage("Align with target destination");*/