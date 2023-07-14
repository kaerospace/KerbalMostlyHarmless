using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace kspFSD
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class fsdSupercruise : PartModule
    {
        public static bool vesselIsSupercruising = false;
        private double previousVelocity = 0.0d;
        private double[] fsdSpeedRange = {30000.0d, 5995849160.0d}; //max out at 20c
        private double supercruiseTargetVel = 0.0d;
        private Vector3 movementVector;
        public static double currentVel = 30000.0d;
        private double[] smoothAccelerationRange = { 0.01d, 0.001d };
        private double smoothDeceleration = 0.01d;
        [KSPEvent(groupName = "FSDControls", guiActive = true, active = true, guiActiveEditor = false, guiName = "Toggle Supercruise", guiActiveUnfocused = false)]
        public void toggleSupercruise()
        {
            Quaternion vesselOrientation = FlightGlobals.ActiveVessel.GetTransform().rotation;
            double movementVel = (double)movementVector.magnitude;
            List<Part> partsList = FlightGlobals.ActiveVessel.parts;
            CelestialBody gravityWell = FlightGlobals.ActiveVessel.mainBody;
            if (!(FlightGlobals.ActiveVessel.altitude < (gravityWell.minOrbitalDistance - gravityWell.Radius + 10000.0d)))
            {
                vesselIsSupercruising = !vesselIsSupercruising;
                if (!vesselIsSupercruising)
                {
                    FlightGlobals.ActiveVessel.IgnoreGForces(1);
                    FlightGlobals.ActiveVessel.ChangeWorldVelocity((vesselOrientation * new Vector3(0.0f, (float)previousVelocity, 0.0f)) - movementVector);
                    foreach (Part part in partsList)
                    {
                        if (part.attachJoint != null)
                        {
                            part.attachJoint.SetUnbreakable(false, false);
                        }
                    }
                }
                else
                {
                    previousVelocity = movementVel;
                    currentVel = 30000.0d;
                    foreach (Part part in partsList)
                    {
                        if (part.attachJoint != null)
                        {
                            part.attachJoint.SetUnbreakable(true, true);
                        }
                    }
                }
            }
        }
        [KSPAction(guiName = "Toggle Supercruise", isPersistent = true)]
        public void toggleSupercruiseAction(KSPActionParam param)
        {
            toggleSupercruise();
        }
        public void Update()
        {
            this.movementVector = FlightGlobals.ActiveVessel.GetObtVelocity();
            Quaternion vesselOrientation = FlightGlobals.ActiveVessel.GetTransform().rotation;
            Vector3 movementVector = FlightGlobals.ActiveVessel.GetObtVelocity();
            double movementVel = (double)movementVector.magnitude;
            float throttleLevel = FlightInputHandler.state.mainThrottle;
            double polynomialFactor = 149895479 / 250;
            CelestialBody gravityWell = FlightGlobals.ActiveVessel.mainBody;
            List<Part> partsList = FlightGlobals.ActiveVessel.parts;
            if (vesselIsSupercruising)
            {
                if ((FlightGlobals.ActiveVessel.altitude < (gravityWell.minOrbitalDistance - gravityWell.Radius + 10000.0d)))
                {
                    vesselIsSupercruising = false;
                    FlightGlobals.ActiveVessel.IgnoreGForces(1);
                    FlightGlobals.ActiveVessel.ChangeWorldVelocity(-movementVector);
                    foreach (Part part in partsList)
                    {
                        if (part.attachJoint != null)
                        {
                            part.attachJoint.SetUnbreakable(false, false);
                        }
                    }
                    ScreenMessages.PostScreenMessage("Emergency Drop: Too Close");
                }
                else
                {
                    supercruiseTargetVel = polynomialFactor * Math.Pow(throttleLevel * 100, 2) + fsdSpeedRange[0]; //Polynomial
                    //supercruiseTargetVel = (fsdSpeedRange[1] - fsdSpeedRange[0]) * throttleLevel + fsdSpeedRange[0]; //Linear
                    double smoothAcceleration = throttleLevel * (smoothAccelerationRange[1] - smoothAccelerationRange[0]) + smoothAccelerationRange[0]; 
                    if ((currentVel < supercruiseTargetVel) && !PauseMenu.isOpen)
                    {
                        currentVel += currentVel * smoothAcceleration;
                    }
                    else if ((currentVel > supercruiseTargetVel) && !PauseMenu.isOpen)
                    {
                        currentVel -= currentVel * smoothDeceleration;
                    }
                    if (currentVel < fsdSpeedRange[0])
                    {
                        currentVel = fsdSpeedRange[0];
                    }
                    else if (currentVel > fsdSpeedRange[1])
                    {
                        currentVel = fsdSpeedRange[1];
                    }
                    FlightGlobals.ActiveVessel.IgnoreGForces(1);
                    if (FlightGlobals.ActiveVessel == vessel)
                    {
                        vessel.ChangeWorldVelocity((vesselOrientation * new Vector3(0.0f, (float)currentVel, 0.0f)) - movementVector);
                    }
                    if (!PauseMenu.isOpen)
                    {
                        TimeWarp.SetRate(0, true, false);
                    }
                }
            }
        }
    }
    public class fsdHyperspaceJump : PartModule
    {
        [KSPEvent(groupName = "FSDControls", guiActive = true, active = true, guiActiveEditor = false, guiName = "Hyperspace Jump", guiActiveUnfocused = false)]
        public void hyperspaceJump()
        {
            CelestialBody targetDestination = FlightGlobals.ActiveVessel.patchedConicSolver.targetBody;
            CelestialBody gravityWell = FlightGlobals.ActiveVessel.mainBody;
            if (targetDestination != null && (FlightGlobals.ActiveVessel.altitude > (gravityWell.minOrbitalDistance - gravityWell.Radius + 10000.0d)))
            {
                Vector3 movementVector = FlightGlobals.ActiveVessel.GetObtVelocity();
                List<Part> partsList = FlightGlobals.ActiveVessel.parts;
                foreach (Part part in partsList)
                {
                    if (part.attachJoint != null)
                    {
                        part.attachJoint.SetUnbreakable(true, true);
                    }
                }
                FlightGlobals.ActiveVessel.ChangeWorldVelocity(-movementVector);
                Orbit deployOrbit = new Orbit(0, 0, targetDestination.Radius * 2, 0, 0, 0, 0, targetDestination);
                Vector3 deployPosition = deployOrbit.getPositionAtUT(Planetarium.GetUniversalTime());
                OrbitPhysicsManager.HoldVesselUnpack(60);
                FlightGlobals.ActiveVessel.IgnoreGForces(10);
                FlightGlobals.ActiveVessel.IgnoreSpeed(10);
                FlightGlobals.ActiveVessel.SetPosition(deployPosition);
                FlightGlobals.ActiveVessel.ChangeWorldVelocity(-movementVector);
                fsdSupercruise.vesselIsSupercruising = true;
                fsdSupercruise.currentVel = 30000.1d;
                foreach (Part part in partsList)
                {
                    if (part.attachJoint != null)
                    {
                        part.attachJoint.SetUnbreakable(false, false);
                    }
                }
                FlightGlobals.ActiveVessel.rootPart.AimCamera();
                FloatingOrigin.ResetTerrainShaderOffset();
                FloatingOrigin.SetOffset(FlightGlobals.ActiveVessel.GetWorldPos3D());
            }  
        }
        [KSPAction(guiName = "Hyperspace Jump", isPersistent = true)]
        public void performJump(KSPActionParam param)
        {
            hyperspaceJump();
        }
    }
}
