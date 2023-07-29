/*-----------------------------------------
Source code copyright: All Rights Reserved
https://github.com/kaerospace, 2023
-----------------------------------------*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using UnityEngine;
using KSP.UI.Screens.Flight;
using UnityEngine.Diagnostics;

#region Silencers
#pragma warning disable IDE0044
#pragma warning disable IDE0060
#pragma warning disable IDE1006
#endregion

namespace kLeapDrive
{
    [KSPModule("Leap Drive")]
    public class ModuleLeapDrive : PartModule
    {
        #region Variables
        private int propellantID;
        private bool disengageAtTgt = false;
        private sbyte flightInfoStatus = -1; //-1: Not Supercruising, 0: Supercruising, 1: Ready to drop out at target
        private double distanceFromTarget;
        private SpeedDisplay speedDisplay;
        private Dictionary<CelestialBody, double> cbDistsToWarpLoc = new Dictionary<CelestialBody, double>();
        private ModuleChargeGenerator generator; //There should not be more than one per part anyway
        #endregion

        #region Constants
        public readonly static Color alertColor = new Color(1.0f, 0.65f, 0.0f);
        private readonly static Color defaultTitleColor = new Color(0.0f, 1.0f, 0.0f);
        private readonly static int c = 299_792_458;
        private readonly static float heightAboveMin = 10_000.0f;
        private readonly static float destinationLockRange = 300_000.0f; //ED Lock-Range is ~1Mm (1_000_000), default reduced to 300km for scale reasons
        private readonly static float rendezvousDistance = 8_000.0f;
        private readonly static float limiterFactor = 0.001f;
        private readonly static float accelerationRate = 0.5f;
        private readonly static float maximumSafeDisengageSpeed = 1_000_000; //1 Mm/sec
        private readonly static float[] speedRange = { 30000.0f, 2.0f * c }; //Max out at 2c, 2001c (speed limit in ED) is too high for KSP to handle
        #endregion

        #region KSPFields
        [KSPField]
        public double MassLimit = double.MaxValue;
        [KSPField]
        public double MinJumpTargetMass = 0.0d; //Limits minimum target mass, ex. Asteroid moons shouldn't be able to be jumped to, as their gravity is too weak
        [KSPField]
        public double SCFuelRate = 0.0d; //Consume a flat amount of fuel no matter the speed, in Elite Dangerous this rate is determined by the powerplant specifics, which is not modeled here.
        [KSPField]
        public double MinJumpFuelUsage = 0.0d; //In-System jumps are very cheap if calculating based on LY, to balance it, a base cost is needed
        [KSPField]
        public double FuelPerLs = 0.0d; //1 Ls = 1 Light Second, ~300_000 km
        [KSPField]
        public string FuelResource = "LiquidFuel";
        [KSPField]
        public bool AllowNonStellarTargets = true; //Makes drive act like Capital Ship FSDs in ED, allowing you to jump to any target
        [KSPField(guiActive = false, guiActiveEditor = false, guiName = "Distance to Target")]
        public string targetDistanceString = "No Target";
        [KSPField(guiActive = false, guiActiveEditor = true, guiName = "Vessel within FTL Mass Limit")]
        public bool canEngage = false;
        [KSPField(guiActive = false, guiActiveEditor = false, guiName = "Can disengage safely")]
        public bool safeDisengage = false;
        [KSPField(guiActive = true, guiActiveEditor = false, isPersistant = true, guiName = "Supercruise on Jump")]
        [UI_Toggle(enabledText = "Yes", disabledText = "No")]
        public bool autoSCOnJump = true;
        [KSPField(guiActive = true, guiActiveEditor = false, isPersistant = true, guiName = "Cancel Angular Momentum")]
        [UI_Toggle(enabledText = "Yes", disabledText = "No")]
        public bool cancelAngularMomentum = true;
        [KSPField(isPersistant = true)]
        public bool vesselIsSupercruising = false;
        [KSPField(isPersistant = true)]
        private float currentVel = 30000.0f;
        [KSPField(isPersistant = true)]
        private float desiredVel = 30000.0f;
        [KSPField(isPersistant = true)]
        private float limitVel = 30000.0f;
        #endregion

        //Sets the module description in the editor (finding this function took ages, send help)
        public override string GetInfo()
        {
            return
$@"<b>Max. Vessel Mass:</b> {MassLimit:N2} t

<b><color=#99FF00>Supercruise</color></b>
<b>Min. Speed:</b> {FormatVelocity(speedRange[0])}
<b>Max. Speed:</b> {FormatVelocity(speedRange[1])}
<b>Safe Disengage:</b> < {FormatVelocity(maximumSafeDisengageSpeed)}
<color=#99FF00>Propellant:</color>
- <b>{FuelResource}:</b> {SCFuelRate:N3}/sec.

<b><color=#99FF00>Leaping</color></b>
{(AllowNonStellarTargets ? $"<b>Can leap to any body </b>\n<i>(with Mass > {MinJumpTargetMass:E2} kg)</i>" : "<b>Can only leap to stars</b>")}
<color=#99FF00>Propellant:</color>
- <b>{FuelResource}</b>
Minimum {MinJumpFuelUsage:N1}
or {FuelPerLs:N1} per light sec.
<color=#FFAB0F>(whichever is greater)</color>";
        }

        //Set up editor event hook
        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            GameEvents.onEditorShipModified.Add(onEditorShipModified);
        }

        //Store LF ID to avoid getting the definition every frame
        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            Debug.Log(FuelResource);
            propellantID = PartResourceLibrary.Instance.GetDefinition(FuelResource).id;
        }

        //Display whether or not the ship is in the mass limit for convenience
        public void onEditorShipModified(ShipConstruct sc)
        {
            canEngage = sc.GetTotalMass() < MassLimit;
            MonoUtilities.RefreshContextWindows(part);
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
            generator = part.FindModuleImplementing<ModuleChargeGenerator>();
            if (part.vessel.GetTotalMass() > MassLimit)
            {
                ScreenMessages.PostScreenMessage("Vessel exceeds mass limit, cannot engage!", 3.0f, ScreenMessageStyle.UPPER_CENTER, alertColor);
                goto failure;
            }
            if (part.vessel.altitude < (part.vessel.mainBody.minOrbitalDistance - part.vessel.mainBody.Radius + heightAboveMin))
            {
                ScreenMessages.PostScreenMessage("Mass Locked, cannot engage!", 3.0f, ScreenMessageStyle.UPPER_CENTER, alertColor);
                goto failure;
            }
            foreach (ModuleLeapDrive core in part.vessel.FindPartModulesImplementing<ModuleLeapDrive>())
            {
                if (core.vesselIsSupercruising && core != this) return;
            }
            SetSCState(!vesselIsSupercruising && generator.FillStatus(true));
            return;
            //Rare acceptable use of goto
            failure:
            vesselIsSupercruising = false;
            return;
        }

        //Switch between Supercruise States
        public void SetSCState(bool state)
        {
            if (vesselIsSupercruising == state) return;
            generator = part.FindModuleImplementing<ModuleChargeGenerator>();
            vesselIsSupercruising = state;
            if (!state)
            {
                Fields["safeDisengage"].guiActive = false;
                Fields["targetDistanceString"].guiActive = false;
                part.vessel.IgnoreGForces(3);
                flightInfoStatus = -1;
                if (disengageAtTgt)
                {
                    FlightGlobals.fetch.SetShipOrbitRendezvous(part.vessel.targetObject.GetVessel(), UnityEngine.Random.onUnitSphere * rendezvousDistance, Vector3d.zero);
                }
                Debug.Log(currentVel);
                Debug.Log(currentVel > maximumSafeDisengageSpeed);
                //Breaks your ship if you go too fast, because balancing or something
                if (currentVel > maximumSafeDisengageSpeed && !CheatOptions.NoCrashDamage)
                {
                    ScreenMessages.PostScreenMessage("Unsafe Disengage, too fast!", 3.0f, ScreenMessageStyle.UPPER_CENTER, Color.red);
                    for (int i = 0; i < Math.Max(3, part.vessel.parts.Count/20); i++)
                    {
                        part.vessel.Parts[UnityEngine.Random.Range(0, part.vessel.Parts.Count)].explode();
                    }
                }
                else OrbitMagic();
                currentVel = speedRange[0];
                FlightGlobals.SetSpeedMode(FlightGlobals.SpeedDisplayModes.Orbit);
                speedDisplay.textTitle.color = defaultTitleColor;
            }
            else
            {
                GetSpeedDisplay();  
                currentVel = speedRange[0];
                flightInfoStatus = 0;
                Fields["targetDistanceString"].guiActive = true;
                Fields["safeDisengage"].guiActive = true;
            }
            //Nuh uh, you need to charge again ;)
            if (generator.IsActivated) generator.StopResourceConverter();
        }

        //The Heart of Supercruise
        public void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (vesselIsSupercruising)
            {
                //Requirement Checks
                if (part.vessel != FlightGlobals.ActiveVessel) goto exit;
                if (!ConsumeResource(propellantID, SCFuelRate * Time.fixedDeltaTime))
                {
                    ScreenMessages.PostScreenMessage("Emergency Drop: No Fuel", 3.0f, ScreenMessageStyle.UPPER_CENTER, alertColor);
                    goto exit;
                }
                if ((part.vessel.altitude < part.vessel.mainBody.minOrbitalDistance - part.vessel.mainBody.Radius + heightAboveMin))
                {
                    //I don't think this ever gets triggered
                    ScreenMessages.PostScreenMessage("Emergency Drop: Too Close", 3.0f, ScreenMessageStyle.UPPER_CENTER, alertColor);
                    goto exit;
                }
                //Supercruise Code
                float throttleLevel = part.vessel.ctrlState.mainThrottle;
                limitVel = GetLimitVelocity();
                desiredVel = Mathf.Clamp(throttleLevel * limitVel, speedRange[0], speedRange[1]);
                currentVel = Mathf.Clamp(Mathf.Lerp(currentVel, desiredVel, accelerationRate * Time.fixedDeltaTime), speedRange[0], limitVel);
                safeDisengage = currentVel < maximumSafeDisengageSpeed;
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
                    ScreenMessages.PostScreenMessage("Emergency Drop: Impact Imminent", 3.0f, ScreenMessageStyle.UPPER_CENTER, alertColor);
                    goto exit;
                }
                disengageAtTgt = (part.vessel.targetObject is object) ? RendezvousCheck(out targetDistanceString) : NoTarget();
                if (disengageAtTgt) flightInfoStatus = 1; else flightInfoStatus = 0;
                if (!PauseMenu.isOpen)
                {
                    FlightInputHandler.fetch.stageLock = true;
                    if (TimeWarp.CurrentRateIndex != 0 && TimeWarp.WarpMode != TimeWarp.Modes.LOW) TimeWarp.SetRate(0, true, postScreenMessage: false);
                }
                SetSpeedDisplay();
                return;
                //In case requirements were not met, leave SC
                exit:
                SetSCState(false);
                return;
            }
        }

        [KSPEvent(guiActive = true, active = true, guiActiveEditor = false, guiName = "Fix Speed Display", guiActiveUnfocused = false, isPersistent = false)]
        public void GetSpeedDisplay()
        {
            speedDisplay = GameObject.FindObjectOfType<SpeedDisplay>();
        }


        //Navball Updates
        public void LateUpdate()
        {
            if (vesselIsSupercruising)
            {
                //Sometimes this breaks, not sure why
                SetSpeedDisplay();
            }
        }

        public void Update()
        {
            if (cancelAngularMomentum)
            {
                vessel.angularVelocity.Zero();
            }
        }

        public bool NoTarget() { targetDistanceString = "No Target"; return false; }

        //Display our velocity
        public void SetSpeedDisplay()
        {
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

            speedDisplay.textSpeed.text = FormatVelocity(currentVel);
            //Is this overcomplicated?
        }

        //Format the velocity to better fit the large speeds we are dealing with
        public string FormatVelocity(float vel)
        {
            switch (vel)
            {
                case float n when n < 1_000_000: return (vel / 1000).ToString("F1") + " km/s";
                case float n when n < 0.1 * c: return (vel / 1000000).ToString("F2") + " Mm/s"; ;
                default: return (vel / c).ToString("F2") + " c"; 
            }
        }

        //Format the distance to better fit the distances we are dealing with
        public string FormatDistance(double dst)
        {
            dst = Math.Abs(dst);
            switch (dst)
            {
                case double n when n < 1_000_000: return (dst / 1000).ToString("F1") + " km";
                case double n when n < 0.1 * c: return (dst / 1000000).ToString("F2") + " Mm";
                default: return (dst / c).ToString("F2") + " Ls";
            }
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

        //Determine if you can "lock" and rendezvous with the selected target
        public bool RendezvousCheck(out string distanceString)
        {
            distanceFromTarget = (part.vessel.GetWorldPos3D() - (Vector3d)part.vessel.targetObject?.GetTransform().position).magnitude;
            distanceString = $"{FormatDistance(distanceFromTarget)}";
            Vessel lockedTgt = part.vessel.targetObject?.GetVessel();
            if (lockedTgt is object)
            {
                if (distanceFromTarget > destinationLockRange || lockedTgt.mainBody != part.vessel.mainBody) return false;
                if (!LineAndSphereIntersects(part.vessel.GetWorldPos3D(), lockedTgt.GetWorldPos3D(), part.vessel.mainBody.position, part.vessel.mainBody.minOrbitalDistance)) return true;
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
        [KSPEvent(guiActive = true, active = true, guiActiveEditor = false, guiName = "Perform Hyperspace Leap", guiActiveUnfocused = false)]
        public void CommenceJumpSequence()
        {
            generator = part.FindModuleImplementing<ModuleChargeGenerator>();
            CelestialBody targetDestination = part.vessel.patchedConicSolver.targetBody;
            //A bit many if statements, but I don't think theres a cleaner way
            if (part.vessel.altitude < part.vessel.mainBody.minOrbitalDistance - part.vessel.mainBody.Radius) { ScreenMessages.PostScreenMessage("Mass Locked, cannot engage!", 3.0f, ScreenMessageStyle.UPPER_CENTER, alertColor); return; }
            if (part.vessel.GetTotalMass() > MassLimit) { ScreenMessages.PostScreenMessage("Vessel exceeds Mass Limit, cannot engage!", 3.0f, ScreenMessageStyle.UPPER_CENTER, alertColor); return; }
            if (targetDestination is null || targetDestination == part.vessel.mainBody || !(AllowNonStellarTargets || targetDestination.isStar)) { ScreenMessages.PostScreenMessage("Cannot Leap, Invalid Target", 3.0f, ScreenMessageStyle.UPPER_CENTER, alertColor); return; }
            if (targetDestination.Mass < MinJumpTargetMass) { ScreenMessages.PostScreenMessage("Cannot Leap, Target too small", 3.0f, ScreenMessageStyle.UPPER_CENTER, alertColor); return; }
            Vector3d jumpVector = targetDestination.position - part.vessel.GetWorldPos3D();
            if (!NearCollinearCheck(part.vessel.transform.up, jumpVector, 5.0f)) { ScreenMessages.PostScreenMessage("Align with Target Destination", 3.0f, ScreenMessageStyle.UPPER_CENTER, alertColor); return; }
            //Doesn't work for some reason: if (LineAndSphereIntersects(part.vessel.GetWorldPos3D(), targetDestination.position, part.vessel.mainBody.position, part.vessel.mainBody.minOrbitalDistance)) { ScreenMessages.PostScreenMessage("Target Obscured", 3.0f, ScreenMessageStyle.UPPER_CENTER, alertColor); return; }
            double fuelRequired = Math.Max(MinJumpFuelUsage, (jumpVector.magnitude / c) * FuelPerLs);
            //Ew, nested if statement
            if (generator.FillStatus())
            {
                if (ConsumeResource(propellantID, fuelRequired, true))
                {
                    generator.StopResourceConverter();
                    HyperspaceLeap(targetDestination);
                }
                else ScreenMessages.PostScreenMessage($"Insufficient Fuel for Jump, need {fuelRequired:F0} {FuelResource}", 3.0f, ScreenMessageStyle.UPPER_CENTER, alertColor);
            }
        }

        //Actually leaps the ship
        public void HyperspaceLeap(CelestialBody target)
        {
            vesselIsSupercruising = false;
            part.vessel.GoOnRails();
            FlightGlobals.fetch.SetShipOrbit(target.flightGlobalsIndex, 0, target.minOrbitalDistance * 2, 0, 0, 0, 0, 0);
            //ED points you at the star, but I'm feeling nice :)
            Vector3d radial = part.vessel.GetWorldPos3D() - part.vessel.mainBody.position;
            part.vessel.SetRotation(Quaternion.LookRotation(RandomOrthoVector(radial), radial)); //Points outwards for some reason, but that's what we want, so...
            //Post-Jump dethrottle so you don't go where you don't want to go (like into a star)
            currentVel = speedRange[0];
            part.vessel.GoOffRails();
            SetSCState(autoSCOnJump);
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
            return Vector3.Angle(v1, v2) < threshold;
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

    public class ModuleChargeGenerator : ModuleResourceConverter
    {
        #region Variables
        public PartResource chargeResource;
        #endregion

        #region Constants
        private readonly static char barUnit = '/';
        private readonly static int width = 20;
        private ScreenMessage msg = null;
        #endregion

        //Get Charge Resource so we don't have to get it again later
        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            chargeResource = part.Resources.Get(outputList[0].ResourceName);  //There should only be one output resource, ignore all other ones
        }

        //Drain Charge when stopping
        public override void StopResourceConverter()
        {
            chargeResource.amount = 0;
            base.StopResourceConverter();
        }

        //Drain Charge when stopping, this time for the Action
        public override void StopResourceConverterAction(KSPActionParam param)
        {
            chargeResource.amount = 0;
            base.StopResourceConverterAction(param);
        }

        //Draw a progress bar for style points
        public void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (IsActivated)
            {
                if (msg is null) msg = ScreenMessages.PostScreenMessage("test", 1, ScreenMessageStyle.UPPER_CENTER);
                
                double chargeRatio = chargeResource.amount / chargeResource.maxAmount;
                int barCharge = (int)(chargeRatio * width);
                string progressBar = $"<color=#FFA500>[{new string(barUnit, barCharge)}<color=#000000>{new string(barUnit, width - barCharge)}</color>]</color>";
                ScreenMessages.RemoveMessage(msg);
                msg = ScreenMessages.PostScreenMessage(progressBar, 1.0f, ScreenMessageStyle.UPPER_CENTER);
            }
            else if (msg is object) ScreenMessages.RemoveMessage(msg);
        }

        //Returns true if full, optionally automatically discharges. This stuff might be a bit pvercomplicated, but it fits the current implementation the best
        public bool FillStatus(bool dischargeIfFull = false)
        {
            bool status = chargeResource.amount == chargeResource.maxAmount;
            if (!status) { ScreenMessages.PostScreenMessage($"Drive needs to be charged!", 3.0f, ScreenMessageStyle.UPPER_CENTER, ModuleLeapDrive.alertColor); return false; }
            else if (dischargeIfFull) StopResourceConverter();
            return status;
        }
    }

    public class ModuleSCFX : PartModule
    {
        #region Variable (Singular)
        public ParticleSystem particles;
        #endregion

        #region KSPFields

        #endregion

        //Only show the particles on the active vessel
        public void Update()
        {
            if (FlightGlobals.ActiveVessel != part.vessel || !HighLogic.LoadedSceneIsFlight) Destroy(particles);
        }

        //Use Unity Particle System instead of KSP built-in stuff, it works better
        [KSPEvent(guiActive = true, active = true, guiActiveEditor = false, guiName = "Start SCFX [DEBUG]", guiActiveUnfocused = false, isPersistent = false)]
        public void SetupParticleSystem()
        {
            particles = part.vessel.gameObject.AddOrGetComponent<ParticleSystem>();
            //Get all the modules
            ParticleSystemRenderer renderer = particles.GetComponent<ParticleSystemRenderer>();
            ParticleSystem.MainModule _main = particles.main;
            ParticleSystem.ShapeModule _shape = particles.shape;
            ParticleSystem.EmissionModule _emission = particles.emission;
            ParticleSystem.TrailModule _trails = particles.trails;
            //Set Main
            _main.startSize = 1.0f;
            _main.startSpeed = 100.0f;
            //Set Emitter Cylinder
            _shape.shapeType = ParticleSystemShapeType.Cone;
            _shape.angle = 0;
            _shape.radius = Mathf.Max(part.vessel.vesselSize.x, part.vessel.vesselSize.z) * 1.0f;
            _shape.radiusThickness = 0;
            _shape.rotation = new Vector3(90.0f, 0.0f, 0.0f);
            _shape.position = new Vector3(0.0f, 100.0f, 0.0f);
            //Enable Emission
            _emission.rateOverTime = 10.0f;
            _emission.enabled = true;
            //Trail Width Curve
            AnimationCurve _curve = new AnimationCurve();
            _curve.AddKey(0.0f, 1.0f);
            _curve.AddKey(1.0f, 0.0f);
            //Set Trails
            _trails.ratio = 0.9f;
            _trails.lifetime = new ParticleSystem.MinMaxCurve(0.02f, 0.1f);
            _trails.widthOverTrail = new ParticleSystem.MinMaxCurve(0.2f, _curve);
            _trails.enabled = true;
            //Set Particle Material
            Texture2D tex = GameDatabase.Instance.GetTexture("kAerospace/FX/scfx", false);
            Material mat = new Material(Shader.Find("Unlit/Transparent"));
            mat.mainTexture = tex;
            //Set Trail Material
            Material trail = new Material(Shader.Find("Particles/Standard Unlit"));
            trail.color = Color.white;
            //Apply Materials
            renderer.material = mat;
            renderer.trailMaterial = trail;
        }

        //Destroy the Component outright, I don't think keeping it is smart
        [KSPEvent(guiActive = true, active = true, guiActiveEditor = false, guiName = "Destroy SCFX [DEBUG]", guiActiveUnfocused = false, isPersistent = false)]
        public void DestroyParticleSystem()
        {
            Destroy(particles);
        }
    }
}