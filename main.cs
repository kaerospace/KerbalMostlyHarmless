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
        public bool vesselIsSupercruising = false;
        private bool disengageAtTgt = false;
        private sbyte flightInfoStatus = -1; //-1: Not Supercruising, 0: Supercruising, 1: Ready to drop out at target
        private float currentVel = 30000.0f;
        private float desiredVel = 30000.0f;
        private float limitVel = 30000.0f;
        private string formattedVelString;
        private SpeedDisplay speedDisplay;
        private Color defaultTitleColor;
        private Dictionary<CelestialBody, double> cbDistsToWarpLoc = new Dictionary<CelestialBody, double>();
        #endregion

        #region Constants
        private readonly static int c = 299_792_458;
        private readonly static float destinationLockRange = 300_000; //ED Lock-Range is ~1Mm (1_000_000), reduced to 300km for scale reasons
        private readonly static float rendezvousDistance = 8_000;
        private readonly static float speedLimitExponent = 1.2f;
        private readonly static float brakeSOIFactor = 30.0f;
        private readonly static float accelerationRate = 0.5f;
        private readonly static float[] speedRange = { 30000.0f, 2.0f * c }; //Max out at 2c, 2001c (speed limit in ED) is too high for KSP to handle | Just keeping this in case I need it later:ED acceleration rate is ~0.6c/s (57 minutes to reach max speed of 2001c)
        #endregion

        private LineRenderer debugline = null;

        [KSPEvent(guiActive = true, active = true, guiActiveEditor = false, guiName = "Toggle Supercruise", guiActiveUnfocused = false)]
        public void ToggleSupercruise()
        {
            part.vessel = part.vessel;

            CelestialBody gravityWell = part.vessel.mainBody;

            if (!(part.vessel.altitude < (gravityWell.minOrbitalDistance - gravityWell.Radius + 10000.0d)))
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

        [KSPAction(guiName = "Toggle Supercruise", isPersistent = true)]
        public void ToggleSupercruiseAction(KSPActionParam param)
        {
            ToggleSupercruise();
        }

        public void OnGUI()
        {
            if (speedDisplay == null) { speedDisplay = GameObject.FindObjectOfType<SpeedDisplay>(); defaultTitleColor = speedDisplay.textTitle.color; }
        }

        //The Heart of Supercruise
        public void FixedUpdate()
        {
            if (debugline == null)
            {
                debugline = part.gameObject.AddOrGetComponent<LineRenderer>();
                debugline.startWidth = 3;
                debugline.endWidth = 3;
                debugline.positionCount = 3;
            }

            if (vesselIsSupercruising)
            {
                if (part.vessel != FlightGlobals.ActiveVessel) SetSCState(false);

                float throttleLevel = part.vessel.ctrlState.mainThrottle;
                CelestialBody gravityWell = part.vessel.mainBody;

                if ((part.vessel.altitude < (gravityWell.minOrbitalDistance - gravityWell.Radius)))
                {
                    SetSCState(false);
                    ScreenMessages.PostScreenMessage("Emergency Drop: Too Close");
                }

                else
                {
                    limitVel = GetLimitVelocity();
                    //Debug.Log(limitVel);
                    desiredVel = Mathf.Clamp(throttleLevel * limitVel, speedRange[0], speedRange[1]);
                    currentVel = Mathf.Lerp(currentVel, desiredVel, accelerationRate * Time.fixedDeltaTime);

                    Vector3d translatedVector = part.vessel.GetWorldPos3D() + part.vessel.transform.up.normalized * currentVel * Time.fixedDeltaTime;

                    cbDistsToWarpLoc.Clear();
                    foreach (CelestialBody b in FlightGlobals.Bodies)
                    {
                        cbDistsToWarpLoc.Add(b, (translatedVector - b.position).sqrMagnitude);
                    }

                    CelestialBody closestBody = cbDistsToWarpLoc.Aggregate((l, r) => l.Value < r.Value ? l : r).Key;

                    if (cbDistsToWarpLoc[closestBody] > Math.Pow(closestBody.minOrbitalDistance, 2))
                    {
                        if (FlightGlobals.VesselsLoaded.Count > 1) part.vessel.SetPosition(translatedVector);
                        else FloatingOrigin.SetOutOfFrameOffset(translatedVector);
                    }
                    else
                    {
                        SetSCState(false);
                        ScreenMessages.PostScreenMessage("Emergency Drop: Collision Avoidance");
                    }

                    if (part.vessel.targetObject != null) RendezvousCheck(); else disengageAtTgt = false;
                    if (disengageAtTgt) flightInfoStatus = 1; else flightInfoStatus = 0;

                    if (!PauseMenu.isOpen)
                    {
                        FlightInputHandler.fetch.stageLock = true;
                        TimeWarp.SetRate(0, true, postScreenMessage: false);
                    }
                }
            }
        }

        //Navball Updates
        public void LateUpdate()
        {
            if (vesselIsSupercruising)
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
                        break;
                }
                speedDisplay.textSpeed.text = formattedVelString;
            }
        }

        //Determine the highest possible velocity in the current scenario based on distance to celestial bodies.
        public float GetLimitVelocity()
        {
            var distanceFactors = new List<double>();
            foreach(CelestialBody body in FlightGlobals.Bodies)
            {
                if (!body.isStar)
                {
                    double factor = (part.vessel.GetWorldPos3D() - body.position).magnitude / (body.sphereOfInfluence * brakeSOIFactor);
                    if (factor > 0.0d) { distanceFactors.Add(factor); }
                }
            }
            float largestInfluence = Mathf.Clamp01((float) distanceFactors.Min());
            return Mathf.Clamp((float) (speedRange[1] * Mathf.Pow(largestInfluence, speedLimitExponent)), speedRange[0], speedRange[1]);
        }

        //Switch between Supercruise States
        public void SetSCState(bool state)
        {
            vesselIsSupercruising = state;
            if (!state)
            {
                part.vessel.IgnoreGForces(2);
                flightInfoStatus = -1;
                if (disengageAtTgt)
                {
                    try
                    {
                        
                        FlightGlobals.fetch.SetShipOrbitRendezvous(part.vessel.targetObject.GetVessel(), UnityEngine.Random.onUnitSphere * rendezvousDistance, Vector3d.zero);
                    }
                    catch (Exception e) { Debug.Log(e); }
                }
                else OrbitMagic();

                FlightGlobals.SetSpeedMode(FlightGlobals.SpeedDisplayModes.Orbit);
                speedDisplay.textTitle.color = defaultTitleColor;
            }
            else flightInfoStatus = 0;
        }

        //Determine if you can "lock" and rendezvous with the selected target
        public void RendezvousCheck()
        {
            Vessel lockedTgt = null;
            lockedTgt = part.vessel.targetObject.GetVessel();
            if (lockedTgt != null)
            {         
                Vector3d tgtRelPos = part.vessel.GetWorldPos3D() - lockedTgt.GetWorldPos3D();
                //Debug.Log(tgtRelPos.sqrMagnitude < (destinationLockRange * destinationLockRange));
                if (tgtRelPos.sqrMagnitude < (destinationLockRange * destinationLockRange) && lockedTgt.mainBody == part.vessel.mainBody)
                {
                    if (!LineAndSphereIntersects(part.vessel.GetWorldPos3D(), lockedTgt.GetWorldPos3D(), part.vessel.mainBody.position, part.vessel.mainBody.minOrbitalDistance))
                    {
                        disengageAtTgt = true;
                        return;
                    }
                }
            }
            Debug.Log(disengageAtTgt);
            disengageAtTgt = false;
        }

        //Check if a line segment intersects with a Sphere
        public bool LineAndSphereIntersects(Vector3d p1, Vector3d p2, Vector3d center, double radius)
        {
            double sqrRadius = radius * radius;

            Vector3d a = p1 - center;
            Vector3d b = p2 - center;
            Vector3d c = p2 - p1;

            double sqrDistCenter;
            if (180.0d - Vector3d.Angle(a, c) >= 90.0d) sqrDistCenter = a.sqrMagnitude;
            else if (180.0d - Vector3d.Angle(b, c) >= 90.0d) sqrDistCenter = b.sqrMagnitude;
            else sqrDistCenter = Math.Abs(Vector3d.Cross(a, b).sqrMagnitude / c.sqrMagnitude);
            if (sqrDistCenter < sqrRadius) return true;
            else return false;
        }

        //Does some Dark Magic to determine the Orbit based on the Vessels position and orientation relative to the Planet.
        public void OrbitMagic()
        {
            Orbit currentOrbit = part.vessel.GetCurrentOrbit();
            double currentTime = Planetarium.GetUniversalTime();
            currentOrbit = new Orbit(0.0d, 0.0d, part.vessel.altitude + part.vessel.mainBody.Radius, currentOrbit.LAN, currentOrbit.argumentOfPeriapsis, currentOrbit.meanAnomalyAtEpoch, currentTime, part.vessel.mainBody);
            Vector3d normal = part.vessel.GetWorldPos3D() - part.vessel.mainBody.position;
            //Debug.Log(currentOrbit.semiMajorAxis);
            //Debug.Log(normal.magnitude);
            Vector3d offset = part.vessel.transform.up * 1_00f;
            double lambda = Vector3d.Dot(offset, normal) / normal.sqrMagnitude;
            Vector3d tangentialVector = offset - lambda * normal;
            Debug.Log(currentOrbit.getOrbitalSpeedAt(currentTime));
            Vector3d tangentialVelocity = tangentialVector.normalized * currentOrbit.getOrbitalSpeedAt(currentTime);
            debugline.SetPosition(0, part.vessel.GetWorldPos3D());
            debugline.SetPosition(1, part.vessel.GetWorldPos3D() + offset);
            debugline.SetPosition(2, part.vessel.GetWorldPos3D() + offset - lambda * normal);
            Debug.Log(tangentialVelocity.magnitude.ToString());

            //currentOrbit = Orbit.OrbitFromStateVectors(normal, tangentialVelocity, part.vessel.mainBody, currentTime);
            part.vessel.ChangeWorldVelocity(tangentialVelocity - part.vessel.GetObtVelocity());
            //FlightGlobals.fetch.SetShipOrbit(currentOrbit.referenceBody.flightGlobalsIndex, currentOrbit.eccentricity, currentOrbit.semiMajorAxis, currentOrbit.inclination, currentOrbit.LAN, currentOrbit.meanAnomaly, currentOrbit.argumentOfPeriapsis, currentOrbit.ObT);
        }

        /*
        [KSPEvent(guiActive = true, active = true, guiActiveEditor = false, guiName = "Perform Hyperspace Jump", guiActiveUnfocused = false)]
        public void CommenceJumpSequence()
        {
            Vessel currentVessel = FlightGlobals.ActiveVessel;
            CelestialBody targetDestination = currentVessel.patchedConicSolver.targetBody;
            CelestialBody gravityWell = currentVessel.mainBody;
            //Vector3 diff = currentVessel.patchedConicSolver.targetBody.GetTransform().position - currentVessel.GetTransform().position;

            if (targetDestination != null && FlightGlobals.ActiveVessel.altitude > (gravityWell.minOrbitalDistance - gravityWell.Radius))
            {
                HyperspaceJump(targetDestination);
            }
        }

        [KSPAction(guiName = "Perform Hyperspace Jump", isPersistent = true)]
        public void JumpAction(KSPActionParam param)
        {
            CommenceJumpSequence();
        }

        public void HyperspaceJump(CelestialBody target)
        {
            vesselIsSupercruising = false;
            FlightGlobals.fetch.SetShipOrbit(target.flightGlobalsIndex, 0, target.minOrbitalDistance * 2, 0, 0, 0, 0, 0);
            currentVel = speedRange[0];
        }
        */
    }
    public class kFTLGenerator : PartModule
    {

    }
}

/*public bool NearCollinearCheck(Vector3 v1, Vector3 v2, double threshold)
{
    if (Mathf.Rad2Deg * Mathf.Asin(Vector3.Dot(v1, v2) / v1.magnitude * v2.magnitude) < threshold) return true; else return false;
}*/

/*if (NearCollinearCheck(currentVessel.GetTransform().forward, diff, 5))
{
    if (currentVessel.ctrlState.mainThrottle == 1.0f)
    {
        HyperspaceJump(targetDestination);
    }
    else ScreenMessages.PostScreenMessage("Throttle up to engage");
}
else ScreenMessages.PostScreenMessage("Align with target destination");*/