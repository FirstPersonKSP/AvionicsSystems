﻿//#define COMPARE_PHASE_ANGLE_PROTRACTOR
//#define DEBUG_CHANGE_ALTITUDE
/*****************************************************************************
 * The MIT License (MIT)
 * 
 * Copyright (c) 2016-2018 MOARdV
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to
 * deal in the Software without restriction, including without limitation the
 * rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
 * sell copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
 * DEALINGS IN THE SOFTWARE.
 * 
 ****************************************************************************/
using MoonSharp.Interpreter;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AvionicsSystems
{
    // ΔV - put this somewhere where I can find it easily to copy/paste

    /// <summary>
    /// The MASITransfer class contains functionality equivalent to the KSP
    /// mod Protractor.  However, the code here was written from scratch, since
    /// Protractor is GPL.
    /// </summary>
    /// <LuaName>transfer</LuaName>
    /// <mdDoc>The MASITransfer module does calculations to find phase angles
    /// and ejection angles for Hohmann transfer orbits.  It provides functionality
    /// equivalent to the Protractor mod, but focused strictly on computations
    /// involving the current vessel and a target (either another vessel or a
    /// Celestial Body).
    /// 
    /// Note that MASITransfer assumes the target has a small relative inclination.
    /// It will generate erroneous results for high inclination and retrograde relative
    /// orbits.
    /// 
    /// In addition to providing orbital transfer information, the MASITransfer module
    /// can create maneuver nodes for basic orbital operations (changing Ap or Pe,
    /// circularizing at a specific altitude, plotting Hohmann transfers, etc).
    /// </mdDoc>
    internal class MASITransfer
    {
        private bool invalid = true;

        internal Vessel vessel;
        internal MASVesselComputer vc;

        private bool hohmannTransfer;
        private double currentPhaseAngle;
        private double transferPhaseAngle;
        private double timeUntilTransfer;

        private double ejectionDeltaV;
        private double currentEjectionAngle;
        private double transferEjectionAngle;
        private double timeUntilEjection;

        private double oberthAltitude;
        private double oberthEjectionVelocity;
        private double oberthCurrentEjectionAngle;
        private double oberthTransferEjectionAngle;
        private double oberthTimeUntilEjection;

        private double initialDeltaV;
        private double finalDeltaV;

        private Orbit oUpper = new Orbit();
        private Orbit oLower = new Orbit();
        private Orbit oMid = new Orbit();

        [MoonSharpHidden]
        public MASITransfer(Vessel vessel)
        {
            this.vessel = vessel;
        }

        /// <summary>
        /// The Delta-V section provides information on the amount of velocity
        /// change needed to change orbits.  This information can be computed
        /// based on the current target, or a target altitude, depending on the
        /// specific method called.
        /// 
        /// These values are estimates based on circular orbits, assuming
        /// no plane change is required.  Eccentric orbits, or non-coplanar
        /// orbits, will not reflect the total ΔV required.
        /// </summary>
        #region Transfer Delta-V

        /// <summary>
        /// Returns an estimate of the ΔV required to circularize a Hohmann transfer at
        /// the target's orbit.
        /// 
        /// Negative values indicate a retrograde burn.  Positive values indicate a
        /// prograde burn.
        /// </summary>
        /// <returns>The ΔV in m/s to finialize the transfer.</returns>
        public double DeltaVFinal()
        {
            if (vc.activeTarget != null)
            {
                if (invalid)
                {
                    UpdateTransferParameters();
                }

                return finalDeltaV;
            }
            else
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Returns and estimate of the ΔV required to circularize the vessel's orbit
        /// at the altitude provided.
        /// 
        /// Negative values indicate a retrograde burn.  Positive values indicate a
        /// prograde burn.
        /// </summary>
        /// <param name="destinationAltitude">Destination altitude, in meters.</param>
        /// <returns>ΔV in m/s to circularize at the requested altitude, or 0 if the vessel is not in flight.</returns>
        public double DeltaVFinal(double destinationAltitude)
        {
            if (!(vessel.Landed || vessel.Splashed))
            {
                double GM = vessel.mainBody.gravParameter;
                double rA = vessel.orbit.semiMajorAxis;
                double rB = destinationAltitude + vessel.mainBody.Radius;

                double atx = 0.5 * (rA + rB);
                double Vf = Math.Sqrt(GM / rB);

                double Vtxf = Math.Sqrt(GM * (2.0 / rB - 1.0 / atx));

                return Vf - Vtxf;
            }
            else
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Returns an estimate of the ΔV required to start a Hohmann transfer to
        /// the target's orbit.
        /// 
        /// Negative values indicate a retrograde burn.  Positive values indicate a
        /// prograde burn.
        /// </summary>
        /// <returns>The ΔV in m/s to start the transfer.</returns>
        public double DeltaVInitial()
        {
            if (vc.activeTarget != null)
            {
                if (invalid)
                {
                    UpdateTransferParameters();
                }

                return initialDeltaV;
            }
            else
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Returns and estimate of the ΔV required to change the vessel's orbit
        /// to the altitude provided.
        /// 
        /// Negative values indicate a retrograde burn.  Positive values indicate a
        /// prograde burn.
        /// </summary>
        /// <param name="destinationAltitude">Destination altitude, in meters.</param>
        /// <returns>ΔV in m/s to reach the requested altitude, or 0 if the vessel is not in flight.</returns>
        public double DeltaVInitial(double destinationAltitude)
        {
            if (!(vessel.Landed || vessel.Splashed))
            {
                return DeltaVInitial(vessel.orbit.semiMajorAxis, destinationAltitude + vessel.mainBody.Radius, vessel.mainBody.gravParameter);
            }
            else
            {
                return 0.0;
            }
        }
        #endregion

        /// <summary>
        /// The Ejection Angle region provides information on the ejection angle.  The
        /// ejection angle is used on interplanetary transfers to determine when
        /// the vessel should start its burn to escape the world it currently orbits.
        /// 
        /// When the vessel is orbiting a moon in preparation for an interplanetary
        /// transfer, the target ejection angle will reflect the ejection angle
        /// required to take advantage of the Oberth effect during the ejection.
        /// </summary>
        #region Transfer Ejection Angle

        /// <summary>
        /// Reports the vessel's current ejection angle.  When this value matches
        /// the transfer ejection angle, it is time to start an interplanetary burn.
        /// 
        /// This angle is a measurement of the vessel from the planet's prograde direction.
        /// </summary>
        /// <returns>Current ejection angle in degrees, or 0 if there is no ejection angle.</returns>
        public double CurrentEjectionAngle()
        {
            if (vc.activeTarget != null)
            {
                if (invalid)
                {
                    UpdateTransferParameters();
                }

                return currentEjectionAngle;
            }
            else
            {
                return 0.0;
            }
        }

        /// <summary>
        /// The ΔV required to reach the correct exit velocity for an interplanetary transfer.
        /// </summary>
        /// <returns>ΔV in m/s, or 0 if there is no exit velocity required.</returns>
        public double EjectionVelocity()
        {
            if (vc.activeTarget != null)
            {
                if (invalid)
                {
                    UpdateTransferParameters();
                }

                return ejectionDeltaV;
            }
            else
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Reports the difference between the vessel's current ejection angle
        /// and the transfer ejection angle.  When this value is 0, it is time to
        /// start an interplanetary burn.
        /// </summary>
        /// <returns>Relative ejection angle in degrees, or 0 if there is no ejection angle.</returns>
        public double RelativeEjectionAngle()
        {
            if (vc.activeTarget != null)
            {
                if (invalid)
                {
                    UpdateTransferParameters();
                }

                return Utility.NormalizeAngle(currentEjectionAngle - transferEjectionAngle);
            }
            else
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Provides the time until the vessel reaches the transfer ejection angle.
        /// </summary>
        /// <returns>Time until the relative ejection angle is 0, in seconds, or 0 if there is no ejection angle.</returns>
        public double TimeUntilEjection()
        {
            if (vc.activeTarget != null)
            {
                if (invalid)
                {
                    UpdateTransferParameters();
                }

                return timeUntilEjection;
            }
            else
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Reports the ejection angle when an interplanetary Hohmann transfer
        /// orbit should begin.  This is of use for transfers from one planet
        /// to another - once the transfer phase angle has been reached, the
        /// vessel should launch when the next transfer ejection angle is reached.
        /// </summary>
        /// <returns>Transfer ejection angle in degrees, or 0 if there is no ejection angle.</returns>
        public double TransferEjectionAngle()
        {
            if (vc.activeTarget != null)
            {
                if (invalid)
                {
                    UpdateTransferParameters();
                }

                return transferEjectionAngle;
            }
            else
            {
                return 0.0;
            }
        }

        #endregion

        /// <summary>
        /// The Maneuver Planning region provides functions that can be used to generate
        /// maneuver nodes to accomplish basic orbital tasks.  This capability
        /// does not include autopilot functionality - it is simply a set of helper functions to create
        /// maneuver nodes.
        /// 
        /// The MAS Maneuver Planner is not as full-featured as MechJeb - it does not
        /// work with parabolic / hyperbolic orbits, for instance.
        /// </summary>
        #region Transfer Maneuver Planning

        /// <summary>
        /// Raise or lower the altitude of the apoapsis.  The maneuver node is placed at
        /// periapsis to minimize fuel requirements.  If an invalid apoapsis is supplied
        /// (either by being below the periapsis, or above the SoI of the planet), this function does
        /// nothing.
        /// </summary>
        /// <param name="newAltitude">The new altitude for the apoapsis, in meters.</param>
        /// <returns>1 if a valid maneuver node was created, 0 if it was not.</returns>
        public double ChangeApoapsis(double newAltitude)
        {
            Orbit current = vessel.orbit;
            double newApR = newAltitude + current.referenceBody.Radius;
            if (newApR >= current.PeR && newApR <= current.referenceBody.sphereOfInfluence && vessel.patchedConicSolver != null)
            {
                CelestialBody referenceBody = current.referenceBody;
                double ut = current.timeToPe + Planetarium.GetUniversalTime();
                Vector3d posAtUt = current.getRelativePositionAtUT(ut);
                Vector3d velAtUt = current.getOrbitalVelocityAtUT(ut);
                Vector3d fwdAtUt = velAtUt.normalized;

                double dVUpper;
                double dVLower;

                dVLower = DeltaVInitial(current.PeR, newApR, referenceBody.gravParameter, velAtUt.magnitude);
                if (current.eccentricity >= 1.0 || newApR > current.ApR)
                {
                    // Could possibly set dvUpper to 0?
                    dVUpper = DeltaVInitial(Math.Min(2.0 * newApR, current.referenceBody.sphereOfInfluence), newApR, referenceBody.gravParameter);
                }
                else
                {
                    // Our current orbit brackets the desired altitude, so we'll initialize our search with the
                    // Ap and Pe.  Since the maneuver starts at the Pe, we'll account for actual velocity with
                    // our lower bound value, which should allow us to converge to the solution quicker (or immediately).
                    dVUpper = DeltaVInitial(current.ApR, newApR, referenceBody.gravParameter);
                }
                oUpper.UpdateFromStateVectors(posAtUt, velAtUt + fwdAtUt * dVUpper, referenceBody, ut);
                oLower.UpdateFromStateVectors(posAtUt, velAtUt + fwdAtUt * dVLower, referenceBody, ut);

                double dVMid = (dVUpper + dVLower) * 0.5;
                double apUpper = (oUpper.ApR < 0.0) ? current.referenceBody.sphereOfInfluence : oUpper.ApR;
                double apLower = (oLower.ApR < 0.0) ? current.referenceBody.sphereOfInfluence : oLower.ApR;

#if DEBUG_CHANGE_ALTITUDE
                Utility.LogMessage(this, "Change Ap {0:0.000}:", newAltitude * 0.001);
#endif
                if (Math.Abs(apLower - newApR) < 1.0)
                {
#if DEBUG_CHANGE_ALTITUDE
                    Utility.LogMessage(this, "Close enough - apLower");
#endif
                    // Within 1m of the desired altitude - good enough.
                    // apLower is most likely to be right on, since we're using
                    // the actual orbital velocity @ Pe to determine dV.
                    dVMid = dVLower;
                }
                else if (Math.Abs(apUpper - newApR) < 1.0)
                {
#if DEBUG_CHANGE_ALTITUDE
                    Utility.LogMessage(this, "Close enough - apUpper");
#endif
                    // Within 1m of the desired altitude - good enough.
                    dVMid = dVUpper;
                }
                else
                {
                    oMid.UpdateFromStateVectors(posAtUt, velAtUt + fwdAtUt * dVMid, referenceBody, ut);
                    double apMid = (oMid.ApR < 0.0) ? current.referenceBody.sphereOfInfluence : oMid.ApR;

                    while (Math.Abs(dVUpper - dVLower) > 0.015625)
                    {
#if DEBUG_CHANGE_ALTITUDE
                        Utility.LogMessage(this, " - Upper = {0,6:0.0}m/s -> Pe {1,9:0.000}, Ap {2,9:0.000}", dVUpper, oUpper.PeA * 0.001, oUpper.ApA * 0.001);
                        Utility.LogMessage(this, " - Lower = {0,6:0.0}m/s -> Pe {1,9:0.000}, Ap {2,9:0.000}", dVLower, oLower.PeA * 0.001, oLower.ApA * 0.001);
                        Utility.LogMessage(this, " - Mid   = {0,6:0.0}m/s -> Pe {1,9:0.000}, Ap {2,9:0.000}", dVMid, oMid.PeA * 0.001, oMid.ApA * 0.001);
#endif
                        if (Math.Abs(apLower - newApR) < 1.0)
                        {
                            // Within 1m of the desired altitude - good enough.
                            dVMid = dVLower;
                            // Only for debug printing:
#if DEBUG_CHANGE_ALTITUDE
                            oMid.UpdateFromStateVectors(posAtUt, velAtUt + fwdAtUt * dVMid, referenceBody, ut);
#endif
                            break;
                        }
                        else if (Math.Abs(apUpper - newApR) < 1.0)
                        {
                            // Within 1m of the desired altitude - good enough.
                            dVMid = dVUpper;
                            // Only for debug printing:
#if DEBUG_CHANGE_ALTITUDE
                            oMid.UpdateFromStateVectors(posAtUt, velAtUt + fwdAtUt * dVMid, referenceBody, ut);
#endif
                            break;
                        }
                        else if (Math.Abs(apUpper - newApR) < Math.Abs(apLower - newApR))
                        {
                            apLower = apMid;
                            dVLower = dVMid;

                            Orbit tmp = oLower;
                            oLower = oMid;
                            oMid = tmp;
                        }
                        else
                        {
                            apUpper = apMid;
                            dVUpper = dVMid;

                            Orbit tmp = oUpper;
                            oUpper = oMid;
                            oMid = tmp;
                        }
                        dVMid = (dVUpper + dVLower) * 0.5;
                        oMid.UpdateFromStateVectors(posAtUt, velAtUt + fwdAtUt * dVMid, referenceBody, ut);
                        apMid = (oMid.ApR < 0.0) ? current.referenceBody.sphereOfInfluence : oMid.ApR;
                    }
#if DEBUG_CHANGE_ALTITUDE
                    Utility.LogMessage(this, " - Final = {0,6:0.0}m/s -> Pe {1,9:0.000}, Ap {2,9:0.000}", dVMid, oMid.PeA * 0.001, oMid.ApA * 0.001);
#endif
                }

                Vector3d dV = Utility.ManeuverNode(dVMid, 0.0, 0.0);

                vessel.patchedConicSolver.maneuverNodes.Clear();
                ManeuverNode mn = vessel.patchedConicSolver.AddManeuverNode(ut);
                mn.OnGizmoUpdated(dV, ut);

                return 1.0;
            }

            return 0.0;
        }

        /// <summary>
        /// Change the inclination of the orbit.  The change will be scheduled at either the AN or DN
        /// of the orbit, whichever results in a less expensive maneuver.
        /// </summary>
        /// <param name="newInclination">The new inclination, in degrees.</param>
        /// <returns>1 if the maneuver was scheduled, 0 if it could not be scheduled.</returns>
        public double ChangeInclination(double newInclination)
        {
            Orbit current = vessel.orbit;

            if (MASFlightComputerProxy.ConvertVesselSituation(vessel.situation) > 2 && vessel.patchedConicSolver != null && current.eccentricity < 1.0)
            {
                double deltaI = newInclination - current.inclination;
                double deltaI2 = Utility.NormalizeLongitude(deltaI);

                if (Math.Abs(deltaI2) > 0.0)
                {
                    Vector3d ANVector = current.GetANVector();
                    ANVector.Normalize();

                    double taAN = current.GetTrueAnomalyOfZupVector(ANVector);
                    double timeAN = Utility.NormalizeOrbitTime(current.GetUTforTrueAnomaly(taAN, current.period) - vc.universalTime, current) + vc.universalTime;
                    Vector3d vAN = current.getOrbitalVelocityAtUT(timeAN);

                    double taDN = taAN + Math.PI;
                    double timeDN = Utility.NormalizeOrbitTime(current.GetUTforTrueAnomaly(taDN, current.period) - vc.universalTime, current) + vc.universalTime;
                    Vector3d vDN = current.getOrbitalVelocityAtUT(timeDN);

                    double deltaVScale = Math.Sin(0.5 * Utility.Deg2Rad * deltaI2);

                    // Pick the slower of the two nodes
                    double mTime;
                    double speedAtUt;
                    if (vAN.sqrMagnitude < vDN.sqrMagnitude)
                    {
                        mTime = timeAN;
                        speedAtUt = vAN.magnitude;
                    }
                    else
                    {
                        mTime = timeDN;
                        speedAtUt = vDN.magnitude;
                    }

                    double planeChangedV = 2.0 * speedAtUt * deltaVScale;

                    // This is interesting ... Eq. 4.73 at http://www.braeunig.us/space/ describes the ΔV
                    // needed to change inclination.  However, if I apply that strictly as a ΔV on the
                    // normal, the resulting orbit is not correct.  Looking at the results MechJeb generates,
                    // I notice that there's a retrograde ΔV equal to sin(Θ/2) (which MJ doesn't directly compute -
                    // it's an artifact of its technique).  So, first I determine the prograde component of
                    // the maneuver, and then I apply the balance of the ΔV to the normal.
                    double progradedV = -Math.Abs(deltaVScale * planeChangedV);
                    double normaldV = Math.Sqrt(planeChangedV * planeChangedV - progradedV * progradedV) * Math.Sign(deltaVScale);
                    Vector3d maneuver = Utility.ManeuverNode(progradedV, normaldV, 0.0);

                    vessel.patchedConicSolver.maneuverNodes.Clear();
                    ManeuverNode mn = vessel.patchedConicSolver.AddManeuverNode(mTime);
                    mn.OnGizmoUpdated(maneuver, mTime);

                    return 1.0;
                }
            }

            return 0.0;
        }

        /// <summary>
        /// Raise or lower the altitude of the periapsis.  The maneuver node is placed at
        /// apoapsis to minimize fuel requirements.  If an invalid periapsis is supplied
        /// (either by being above the apoapsis, or lower than the center of the planet, or
        /// if the current orbit is hyperbolic), this function does
        /// nothing.
        /// </summary>
        /// <param name="newAltitude">The new altitude for the periapsis, in meters.</param>
        /// <returns>1 if a valid maneuver node was created, 0 if it was not.</returns>
        public double ChangePeriapsis(double newAltitude)
        {
            Orbit current = vessel.orbit;
            double newPeR = newAltitude + current.referenceBody.Radius;
            if (newPeR >= 0.0 && newPeR <= current.ApR && vessel.patchedConicSolver != null && current.eccentricity < 1.0)
            {
                CelestialBody referenceBody = current.referenceBody;
                double ut = current.timeToAp + Planetarium.GetUniversalTime();
                Vector3d posAtUt = current.getRelativePositionAtUT(ut);
                Vector3d velAtUt = current.getOrbitalVelocityAtUT(ut);
                Vector3d fwdAtUt = velAtUt.normalized;

                double dVUpper;
                double dVLower;

                dVUpper = DeltaVInitial(current.ApR, newPeR, referenceBody.gravParameter, velAtUt.magnitude);
                if (newPeR < current.PeR)
                {
                    // Our current Pe is higher than the target, so we treat it as the upper bound and half the
                    // target Pe as the lower bound.
                    dVLower = DeltaVInitial(newPeR * 0.5, newPeR, referenceBody.gravParameter);
                }
                else
                {
                    // Our current orbit brackets the desired altitude, so we'll initialize our search with the
                    // Ap and Pe.
                    dVLower = DeltaVInitial(current.PeR, newPeR, referenceBody.gravParameter);
                }
                oUpper.UpdateFromStateVectors(posAtUt, velAtUt + fwdAtUt * dVUpper, referenceBody, ut);
                oLower.UpdateFromStateVectors(posAtUt, velAtUt + fwdAtUt * dVLower, referenceBody, ut);

                double dVMid = (dVUpper + dVLower) * 0.5;
                double peUpper = oUpper.PeR;
                double peLower = oLower.PeR;

#if DEBUG_CHANGE_ALTITUDE
                Utility.LogMessage(this, "Change Pe {0:0.000}:", newAltitude * 0.001);
#endif

                if (Math.Abs(peUpper - newPeR) < 1.0)
                {
#if DEBUG_CHANGE_ALTITUDE
                    Utility.LogMessage(this, "Close enough - peUpper");
#endif
                    // Within 1m of the desired altitude - good enough.
                    // peUpper is most likely to be right on, since we're using
                    // the actual orbital velocity @ Ap to determine dV.
                    dVMid = dVUpper;
                }
                else if (Math.Abs(peLower - newPeR) < 1.0)
                {
#if DEBUG_CHANGE_ALTITUDE
                    Utility.LogMessage(this, "Close enough - peLower");
#endif
                    // Within 1m of the desired altitude - good enough.
                    dVMid = dVLower;
                }
                else
                {
                    oMid.UpdateFromStateVectors(posAtUt, velAtUt + fwdAtUt * dVMid, referenceBody, ut);
                    double peMid = oMid.PeR;

                    while (Math.Abs(dVUpper - dVLower) > 0.015625)
                    {
#if DEBUG_CHANGE_ALTITUDE
                        Utility.LogMessage(this, " - Upper = {0,6:0.0}m/s -> Pe {1,9:0.000}, Ap {2,9:0.000}", dVUpper, oUpper.PeA * 0.001, oUpper.ApA * 0.001);
                        Utility.LogMessage(this, " - Lower = {0,6:0.0}m/s -> Pe {1,9:0.000}, Ap {2,9:0.000}", dVLower, oLower.PeA * 0.001, oLower.ApA * 0.001);
                        Utility.LogMessage(this, " - Mid   = {0,6:0.0}m/s -> Pe {1,9:0.000}, Ap {2,9:0.000}", dVMid, oMid.PeA * 0.001, oMid.ApA * 0.001);
#endif
                        if (Math.Abs(peUpper - newPeR) < 1.0)
                        {
                            // Within 1m of the desired altitude - good enough.
                            dVMid = dVLower;
                            // Only for debug printing:
#if DEBUG_CHANGE_ALTITUDE
                            oMid.UpdateFromStateVectors(posAtUt, velAtUt + fwdAtUt * dVMid, referenceBody, ut);
#endif
                            break;
                        }
                        else if (Math.Abs(peLower - newPeR) < 1.0)
                        {
                            // Within 1m of the desired altitude - good enough.
                            dVMid = dVUpper;
                            // Only for debug printing:
#if DEBUG_CHANGE_ALTITUDE
                            oMid.UpdateFromStateVectors(posAtUt, velAtUt + fwdAtUt * dVMid, referenceBody, ut);
#endif
                            break;
                        }
                        else if (Math.Abs(peUpper - newPeR) < Math.Abs(peLower - newPeR))
                        {
                            peLower = peMid;
                            dVLower = dVMid;

                            Orbit tmp = oLower;
                            oLower = oMid;
                            oMid = tmp;
                        }
                        else
                        {
                            peUpper = peMid;
                            dVUpper = dVMid;

                            Orbit tmp = oUpper;
                            oUpper = oMid;
                            oMid = tmp;
                        }
                        dVMid = (dVUpper + dVLower) * 0.5;
                        oMid.UpdateFromStateVectors(posAtUt, velAtUt + fwdAtUt * dVMid, referenceBody, ut);
                        peMid = oMid.PeR;
                    }
#if DEBUG_CHANGE_ALTITUDE
                    Utility.LogMessage(this, " - Final = {0,6:0.0}m/s -> Pe {1,9:0.000}, Ap {2,9:0.000}", dVMid, oMid.PeA * 0.001, oMid.ApA * 0.001);
#endif
                }

                Vector3d dV = Utility.ManeuverNode(dVMid, 0.0, 0.0);

                vessel.patchedConicSolver.maneuverNodes.Clear();
                ManeuverNode mn = vessel.patchedConicSolver.AddManeuverNode(ut);
                mn.OnGizmoUpdated(dV, ut);

                return 1.0;
            }

            return 0.0;
        }

        /// <summary>
        /// Circularize the vessel's orbit at the specified altitude, in meters.  This new altitude
        /// must be between the current periapsis and apoapsis, and the current orbit must not be hyperbolic.
        /// </summary>
        /// <param name="newAltitude">The altitude at which the orbit will be circularized, in meters.</param>
        /// <returns>1 if a node was created, 0 otherwise.</returns>
        public double CircularizeAltitude(double newAltitude)
        {
            Orbit current = vessel.orbit;
            double newSMA = newAltitude + current.referenceBody.Radius;
            if (newSMA >= current.PeR && newSMA <= current.ApR && vessel.patchedConicSolver != null && current.eccentricity < 1.0)
            {
                CelestialBody referenceBody = current.referenceBody;
                double vNew = Math.Sqrt(referenceBody.gravParameter / newSMA);
                double maneuverUt = Planetarium.GetUniversalTime() + Utility.NextTimeToRadius(current, newSMA);

                Vector3d velAtUt = current.getOrbitalVelocityAtUT(maneuverUt);

                Vector3d relativePosition = current.getRelativePositionAtUT(maneuverUt);

                Vector3d prograde;
                Vector3d normal;
                Vector3d radial;
                Utility.GetOrbitalBasisVectors(velAtUt, relativePosition, out prograde, out radial, out normal);

                Vector3d upAtUt = relativePosition.xzy.normalized;
                Vector3d fwdAtUt = Vector3d.Cross(upAtUt, normal);
                Vector3d maneuverVel = fwdAtUt * vNew;

                Vector3d deltaV = maneuverVel - velAtUt.xzy;
                //Utility.LogMessage(this, "dV = {0} because {1} - {2}", deltaV, maneuverVel, velAtUt);
                //Utility.LogMessage(this, "prograde (dot) fwd = {0:0.000}", Vector3d.Dot(prograde, fwdAtUt));

                Vector3d maneuverdV = Utility.ManeuverNode(Vector3d.Dot(deltaV, prograde), Vector3d.Dot(deltaV, normal), Vector3d.Dot(deltaV, radial));

                vessel.patchedConicSolver.maneuverNodes.Clear();
                ManeuverNode mn = vessel.patchedConicSolver.AddManeuverNode(maneuverUt);
                mn.OnGizmoUpdated(maneuverdV, maneuverUt);

                //Vector3d posAtUt = current.getRelativePositionAtUT(maneuverUt);
                //oUpper.UpdateFromStateVectors(posAtUt, maneuverVel.xzy, referenceBody, maneuverUt);
                //Utility.LogMessage(this, "Circularize at {0:0.000}km: {1:0.000} x {2:0.000} @ {3:0.0}",
                //    newAltitude * 0.001,
                //    oUpper.ApA * 0.001, oUpper.PeA * 0.001,
                //    oUpper.inclination);

                return 1.0;
            }

            return 0.0;
        }

        
        /// <summary>
        /// Circularize the vessel's orbit at the specified altitude, in meters.  This new altitude
        /// must be between the current periapsis and the SoI limit. If the orbit is hyperbolic and the craft
        /// is past the periapsis, the altitude must also be greater than the current altitude. This method
        /// uses calculations derived from first principles rather than the KSP orbit functions.
        /// </summary>
        /// <param name="newAltitude">The altitude at which the orbit will be circularized, in meters.</param>
        /// <returns>1 if a node was created, 0 otherwise.</returns>
        public double CircularizeAltitudeHypVis(double newAltitude)
        {
            Orbit current = vessel.orbit;
            double newSMA = newAltitude + current.referenceBody.Radius;

            if ((newSMA >= current.PeR && vc.orbit.timeToPe >= 0 || newSMA >= vessel.altitude) && newSMA <= vessel.mainBody.sphereOfInfluence && vessel.patchedConicSolver != null)
            {
                CelestialBody referenceBody = current.referenceBody;

                // required velocity is entirely circumferential and equal to circular orbit speed

                double vNew = Math.Sqrt(referenceBody.gravParameter / newSMA);

                // get true anomaly when passing through altitude, and time at which that happens

                double tAAtNewSMA = current.TrueAnomalyAtRadius(newSMA);
                double maneuverUt = current.GetUTforTrueAnomaly(tAAtNewSMA, 0);

                // pre-burn speed found using vis-viva equation.
                double vPreBurn = Math.Sqrt(referenceBody.gravParameter * (2 / newSMA - 1 / current.semiMajorAxis));

                // get specific angular momentum and use to find circumferential speed at the burn,
                // and the elevation angle at the burn
                double spAngMom = current.PeR * current.getOrbitalSpeedAtDistance(current.PeR);
                double vPreBurnCirc = spAngMom / newSMA;
                double elevationAngle = Math.Acos(vPreBurnCirc / vPreBurn);

                // delta-V magnitude is the magnitude of the vector difference between pre- and post-burn velocities
                double deltaVTotal = Math.Sqrt(Math.Pow(vNew, 2) + Math.Pow(vPreBurn, 2) - 2 * vNew * vPreBurn * Math.Cos(elevationAngle));

                // orthogonal components of the delta-V (no normal since never going to need inclination change)
                double deltaVPrograde = -(vPreBurn - vNew * Math.Cos(elevationAngle));
                double deltaVRadial = -vNew * Math.Sin(elevationAngle);

                // create the maneuver node
                Vector3d maneuverdV = Utility.ManeuverNode(deltaVPrograde, 0, deltaVRadial);

                vessel.patchedConicSolver.maneuverNodes.Clear();
                ManeuverNode mn = vessel.patchedConicSolver.AddManeuverNode(maneuverUt);
                mn.OnGizmoUpdated(maneuverdV, maneuverUt);

                return 1.0;
            }

            return 0.0;
        }

        /// <summary>
        /// Generate a maneuver to conduct a Hohmann transfer to the current target.  If there is no
        /// target, or the transfer is not a simple transfer to a nearly co-planar target, nothing happens.
        /// 
        /// Results are best when both the vessel and the target are in low-eccentricity orbits.
        /// </summary>
        /// <returns>Returns 1 if a transfer was successfully plotted, 0 otherwise.</returns>
        public double HohmannTransfer()
        {
            if (hohmannTransfer)
            {
                Orbit current = vessel.orbit;
                CelestialBody referenceBody = current.referenceBody;
                double maneuverUt = Planetarium.GetUniversalTime() + timeUntilTransfer;

                Vector3d velAtUt = current.getOrbitalVelocityAtUT(maneuverUt);

                Vector3d relativePosition = current.getRelativePositionAtUT(maneuverUt);

                Vector3d prograde;
                Vector3d normal;
                Vector3d radial;
                Utility.GetOrbitalBasisVectors(velAtUt, relativePosition, out prograde, out radial, out normal);

                Vector3d upAtUt = relativePosition.xzy.normalized;
                Vector3d fwdAtUt = Vector3d.Cross(upAtUt, normal);
                Vector3d maneuverVel = fwdAtUt * (initialDeltaV + Math.Sqrt(referenceBody.gravParameter / relativePosition.magnitude));
                //Utility.LogMessage(this, "insertion v = {0:0.0}, v @ insertion = {1:0.0}", initialDeltaV, velAtUt.magnitude);

                Vector3d deltaV = maneuverVel - velAtUt.xzy;
#if DEBUG_CHANGE_ALTITUDE
                Utility.LogMessage(this, "deltaV = {0} because {1} - {2}", deltaV, maneuverVel, velAtUt.xzy);
#endif
                //Utility.LogMessage(this, "prograde (dot) fwd = {0:0.000}", Vector3d.Dot(prograde, fwdAtUt));

                Vector3d maneuverdV = Utility.ManeuverNode(Vector3d.Dot(deltaV, prograde), Vector3d.Dot(deltaV, normal), Vector3d.Dot(deltaV, radial));
#if DEBUG_CHANGE_ALTITUDE
                Utility.LogMessage(this, "maneuverdV = {0}", maneuverdV);
#endif

                vessel.patchedConicSolver.maneuverNodes.Clear();
                ManeuverNode mn = vessel.patchedConicSolver.AddManeuverNode(maneuverUt);
                mn.OnGizmoUpdated(maneuverdV, maneuverUt);

                return 1.0;
            }

            return 0.0;
        }

        /// <summary>
        /// Match the plane of the target's orbit.  If there is no target, or the target is in a different sphere
        /// of influence, this operation has no effect.
        /// </summary>
        /// <returns>1 if a maneuver was plotted, 0 otherwise.</returns>
        public double MatchPlane()
        {
            Orbit current = vessel.orbit;
            Orbit target = vc.targetOrbit;

            if (MASFlightComputerProxy.ConvertVesselSituation(vessel.situation) > 2 && vessel.patchedConicSolver != null && current.eccentricity < 1.0 && target.referenceBody == current.referenceBody)
            {
                // In the test case I ran, this seemed to be the opposite of the ChangeInclination call.
                // I may need to tweak the sign of deltaI2 based on whether we're changing inclination
                // at the AN or DN.
                double deltaI = current.inclination - target.inclination;
                double deltaI2 = Utility.NormalizeLongitude(deltaI);

                if (Math.Abs(deltaI2) > 0.0)
                {
                    Vector3d vesselNormal = vc.orbit.GetOrbitNormal();
                    Vector3d targetNormal = vc.activeTarget.GetOrbit().GetOrbitNormal();
                    Vector3d cross = Vector3d.Cross(vesselNormal, targetNormal);
                    double taAN = vc.orbit.GetTrueAnomalyOfZupVector(-cross);
                    double timeAN =  Utility.NormalizeOrbitTime(vc.orbit.GetUTforTrueAnomaly(taAN, vc.orbit.period) - vc.universalTime, current) + vc.universalTime;
                    double taDN = vc.orbit.GetTrueAnomalyOfZupVector(cross);
                    double timeDN = Utility.NormalizeOrbitTime(vc.orbit.GetUTforTrueAnomaly(taDN, vc.orbit.period) - vc.universalTime, current) + vc.universalTime;

                    Vector3d vAN = current.getOrbitalVelocityAtUT(timeAN);

                    Vector3d vDN = current.getOrbitalVelocityAtUT(timeDN);

                    double deltaVScale = Math.Sin(0.5 * Utility.Deg2Rad * deltaI2);

                    // Pick the slower of the two nodes
                    double mTime;
                    double speedAtUt;
                    if (vAN.sqrMagnitude < vDN.sqrMagnitude)
                    {
                        mTime = timeAN;
                        speedAtUt = vAN.magnitude;
                    }
                    else
                    {
                        mTime = timeDN;
                        speedAtUt = vDN.magnitude;
                    }

                    double planeChangedV = 2.0 * speedAtUt * deltaVScale;

                    // This is interesting ... Eq. 4.73 at http://www.braeunig.us/space/ describes the ΔV
                    // needed to change inclination.  However, if I apply that strictly as a ΔV on the
                    // normal, the resulting orbit is not correct.  Looking at the results MechJeb generates,
                    // I notice that there's a retrograde ΔV equal to sin(Θ/2) (which MJ doesn't directly compute -
                    // it's an artifact of its technique).  So, first I determine the prograde component of
                    // the maneuver, and then I apply the balance of the ΔV to the normal.
                    double progradedV = -Math.Abs(deltaVScale * planeChangedV);
                    double normaldV = Math.Sqrt(planeChangedV * planeChangedV - progradedV * progradedV) * Math.Sign(deltaVScale);
                    Vector3d maneuver = Utility.ManeuverNode(progradedV, normaldV, 0.0);

                    vessel.patchedConicSolver.maneuverNodes.Clear();
                    ManeuverNode mn = vessel.patchedConicSolver.AddManeuverNode(mTime);
                    mn.OnGizmoUpdated(maneuver, mTime);

                    // TODO: When I'm more awake, I should do this analytically by deciding whether the change
                    // in inclination is normal or anti-normal at the time of the maneuver.
                    Vector3d newVesselNormal = vessel.patchedConicSolver.maneuverNodes[0].nextPatch.GetOrbitNormal();
                    if (Vector3.Angle(newVesselNormal, targetNormal) > Mathf.Abs((float)deltaI))
                    {
                        maneuver.y = -maneuver.y;
                        vessel.patchedConicSolver.maneuverNodes.Clear();
                        mn = vessel.patchedConicSolver.AddManeuverNode(mTime);
                        mn.OnGizmoUpdated(maneuver, mTime);
                    }

                    return 1.0;
                }
            }

            return 0.0;
        }

        /// <summary>
        /// Match velocities with the target at the moment of closest approach.  If there is no target,
        /// or the target is in a different sphere of influence, the operation has no effect.
        /// </summary>
        /// <returns>1 if a maneuver was plotted, 0 otherwise.</returns>
        public double MatchVelocities()
        {
            if (vc.activeTarget != null && vc.targetOrbit.referenceBody == vessel.mainBody && vessel.patchedConicSolver != null && vc.targetClosestUT > Planetarium.GetUniversalTime())
            {
                double maneuverUt = vc.targetClosestUT;
                Orbit current = vessel.orbit;
                Vector3d velAtUt = current.getOrbitalVelocityAtUT(maneuverUt);
                Vector3d tgtAtUt = vc.targetOrbit.getOrbitalVelocityAtUT(maneuverUt);

                Vector3d relativePosition = current.getRelativePositionAtUT(maneuverUt);

                Vector3d prograde;
                Vector3d normal;
                Vector3d radial;
                Utility.GetOrbitalBasisVectors(velAtUt, relativePosition, out prograde, out radial, out normal);

                Vector3d deltaV = tgtAtUt - velAtUt;

                Vector3d maneuverdV = Utility.ManeuverNode(Vector3d.Dot(deltaV, prograde), Vector3d.Dot(deltaV, normal), Vector3d.Dot(deltaV, radial));
#if DEBUG_CHANGE_ALTITUDE
                Utility.LogMessage(this, "maneuverdV = {0}", maneuverdV);
#endif

                vessel.patchedConicSolver.maneuverNodes.Clear();
                ManeuverNode mn = vessel.patchedConicSolver.AddManeuverNode(maneuverUt);
                mn.OnGizmoUpdated(maneuverdV, maneuverUt);

                return 1.0;
            }

            return 0.0;
        }

        /// <summary>
        /// Return from a moon with a resulting altitude over the planet of 'newAltitude', in meters.
        /// If the vessel is orbiting the Sun, this command has no effect.
        /// 
        /// This maneuver may be used to eject from Kerbin (or any other planet) to a solar orbit with
        /// a specified altitude over the sun.
        /// </summary>
        /// <param name="newAltitude">Altitude of the desired orbit around the parent world, in meters.</param>
        /// <returns>1 if the maneuver was plotted, 0 otherwise.</returns>
        public double ReturnFromMoon(double newAltitude)
        {
            // The last condition listed here is because the sun's referenceBody is
            // itself.
            if (vessel.mainBody.referenceBody != null && vessel.patchedConicSolver != null && vessel.mainBody.referenceBody != vessel.mainBody)
            {
                CelestialBody moon = vessel.mainBody;
                CelestialBody planet = moon.referenceBody;

                double newRadius = newAltitude + planet.Radius;
                double returnDeltaV = DeltaVInitial(moon.orbit.semiMajorAxis, newRadius, planet.gravParameter);
                double currentEjectionAngle = ComputeEjectionAngle(vessel.orbit);

                double ejectiondV, returnEjectionAngle, timeUntilReturn;
                UpdateEjectionParameters(vessel.orbit, returnDeltaV, currentEjectionAngle, out ejectiondV, out returnEjectionAngle, out timeUntilReturn);
                
                double maneuverUt = timeUntilReturn + Planetarium.GetUniversalTime();

                Vector3d maneuverdV = Utility.ManeuverNode(ejectiondV, 0.0, 0.0);

                vessel.patchedConicSolver.maneuverNodes.Clear();
                ManeuverNode mn = vessel.patchedConicSolver.AddManeuverNode(maneuverUt);
                mn.OnGizmoUpdated(maneuverdV, maneuverUt);
            }

            return 0.0;
        }

        #endregion

        /// <summary>
        /// The Oberth Effect region provides information specific to taking advantage
        /// of the Oberth Effect when transferring from a moon to another planet.  These
        /// fields assume the vessel will eject from the moon to the `OberthAltitude`
        /// over the planet,
        /// from which it will fire the interplanetary ejection burn.
        /// 
        /// If the vessel is not in a situtation where the Oberth Effect would be applicable,
        /// these fields all return 0.
        /// </summary>
        #region Transfer Oberth Effect

        /// <summary>
        /// The current ejection angle for the moon in degrees.  When this value matches
        /// `TransferOberthEjectionAngle()`, it is time to do the moon ejection burn.
        /// </summary>
        /// <returns>Current ejection angle over the moon in degrees, or 0.</returns>
        public double CurrentOberthEjectionAngle()
        {
            if (vc.activeTarget != null)
            {
                if (invalid)
                {
                    UpdateTransferParameters();
                }

                return oberthCurrentEjectionAngle;
            }
            else
            {
                return 0.0;
            }
        }

        /// <summary>
        /// The preferred altitude over the parent planet for the
        /// interplanetary ejection burn, in meters.
        /// If the vessel's target
        /// is not another world, or the vessel does not currently orbit a moon, returns 0.
        /// </summary>
        /// <returns>Altitude in meters, or 0.</returns>
        public double OberthAltitude()
        {
            if (vc.activeTarget != null)
            {
                if (invalid)
                {
                    UpdateTransferParameters();
                }

                return oberthAltitude;
            }
            else
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Returns the ΔV in m/s required for the Oberth effect transfer ejection burn.
        /// The vessel's altitude orbiting the moon's parent should match `OberthAltitude()`
        /// after this burn.
        /// If the vessel's target
        /// is not another world, or the vessel does not currently orbit a moon, returns 0.
        /// </summary>
        /// <returns>ΔV in m/s or 0.</returns>
        public double OberthEjectionDeltaV()
        {
            if (vc.activeTarget != null)
            {
                if (invalid)
                {
                    UpdateTransferParameters();
                }

                return oberthEjectionVelocity;
            }
            else
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Returns the relative ejection angle for an Oberth effect tranfers.  When this
        /// value reaches 0, it is time to burn.
        /// If the vessel's target
        /// is not another world, or the vessel does not currently orbit a moon, returns 0.
        /// </summary>
        /// <returns>Relative angle in degrees, or 0.</returns>
        public double RelativeOberthEjectionAngle()
        {
            if (vc.activeTarget != null)
            {
                if (invalid)
                {
                    UpdateTransferParameters();
                }

                return Utility.NormalizeAngle(oberthCurrentEjectionAngle - oberthTransferEjectionAngle);
            }
            else
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Returns the time until the ejection burn must begin for an Oberth effect tranfer, in seconds.
        /// If the vessel's target
        /// is not another world, or the vessel does not currently orbit a moon, returns 0.
        /// </summary>
        /// <returns>Time in seconds until the burn, or 0.</returns>
        public double TimeUntilOberthEjectionAngle()
        {
            if (vc.activeTarget != null)
            {
                if (invalid)
                {
                    UpdateTransferParameters();
                }

                return oberthTimeUntilEjection;
            }
            else
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Returns the ejection angle required to initiate an ejection from the moon's orbit
        /// to the moon's parent world for an interplanetary transfer.  If the vessel's target
        /// is not another world, or the vessel does not currently orbit a moon, returns 0.
        /// </summary>
        /// <returns>Required ejection angle in degrees, or 0.</returns>
        public double TransferOberthEjectionAngle()
        {
            if (vc.activeTarget != null)
            {
                if (invalid)
                {
                    UpdateTransferParameters();
                }

                return oberthTransferEjectionAngle;
            }
            else
            {
                return 0.0;
            }
        }
        #endregion

        /// <summary>
        /// The Phase Angle section provides measurements of the phase angle, the
        /// measure of the angle created by drawing lines from the body being
        /// orbited to the vessel and to the target.  This angle shows relative
        /// position of the two objects, and it is continuously changing as long as
        /// the craft are not in the same orbit.
        /// 
        /// To do a Hohmann transfer between orbits, the vessel should initiate a
        /// burn when its current phase angle reaches the transfer phase angle.
        /// Alternatively, when the relative phase angle reaches 0, initiate a burn.
        /// </summary>
        #region Transfer Phase Angle
        /// <summary>
        /// Returns the current phase angle between the vessel and its target.
        /// </summary>
        /// <returns>Current phase angle in degrees, from 0 to 360.</returns>
        public double CurrentPhaseAngle()
        {
            if (vc.activeTarget != null)
            {
                if (invalid)
                {
                    UpdateTransferParameters();
                }

                return currentPhaseAngle;
            }
            else
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Returns the difference (in degrees) between the current phase angle
        /// and the transfer phase angle.  When this value reaches 0, it is time
        /// to start the transfer burn.  If there is no valid target, this value
        /// is 0.
        /// </summary>
        /// <returns>The difference between the transfer phase angle and the current
        /// phase angle in degrees, ranging from 0 to 360.</returns>
        public double RelativePhaseAngle()
        {
            if (vc.activeTarget != null)
            {
                if (invalid)
                {
                    UpdateTransferParameters();
                }

                return Utility.NormalizeAngle(currentPhaseAngle - transferPhaseAngle);
            }
            else
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Returns the time in seconds until the vessel reaches the correct
        /// phase angle for initiating a burn to transfer to the target.
        /// </summary>
        /// <returns>Time until transfer, in seconds, or 0 if there is no solution.</returns>
        public double TimeUntilPhaseAngle()
        {
            if (vc.activeTarget != null)
            {
                if (invalid)
                {
                    UpdateTransferParameters();
                }

                return timeUntilTransfer;
            }
            else
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Returns the phase angle required to initiate a Hohmann
        /// transfer orbit.  This is the absolute phase angle, so it
        /// does not vary over time when comparing two stable orbits.
        /// Use `fc.RelativePhaseAngle()` to count down to a transfer.
        /// 
        /// Returns 0 if there is no active target.
        /// </summary>
        /// <returns>Required phase angle in degrees (always betweeen 0
        /// and 180).</returns>
        public double TransferPhaseAngle()
        {
            if (vc.activeTarget != null)
            {
                if (invalid)
                {
                    UpdateTransferParameters();
                }

                return transferPhaseAngle;
            }
            else
            {
                return 0.0;
            }
        }

        #endregion

#if COMPARE_PHASE_ANGLE_PROTRACTOR
        private static double ProjectAngle2D(Vector3d a, Vector3d b)
        {
            Vector3d ray1 = Vector3d.Project(new Vector3d(a.x, 0.0, a.z), a);
            Vector3d ray2 = Vector3d.Project(new Vector3d(b.x, 0.0, b.z), b);

            double phase = Vector3d.Angle(ray1, ray2);

            Vector3d ap = Quaternion.AngleAxis(90.0f, Vector3d.forward) * a;
            Vector3d ray1p = Vector3d.Project(new Vector3d(ap.x, 0.0, ap.z), ap);
            if (Vector3d.Angle(ray1p, ray2) > 90.0)
            {
                phase = 360.0 - phase;
            }

            return Utility.NormalizeAngle(phase);
        }
        private static double Angle2d(Vector3d vector1, Vector3d vector2)
        {
            Vector3d v1 = Vector3d.Project(new Vector3d(vector1.x, 0, vector1.z), vector1);
            Vector3d v2 = Vector3d.Project(new Vector3d(vector2.x, 0, vector2.z), vector2);
            return Vector3d.Angle(v1, v2);
        }
#endif

        // For a given orbit, find the orbit of the object that orbits the sun.
        // 
        // If the orbit provided orbits the sun, this orbit is returned.  If the
        // orbit is around a body that orbits the sun, the body's orbit is returned.
        // If the orbit is around a moon, return the orbit of the moon's parent.
        static private Orbit GetSolarOrbit(Orbit orbit, out int numHops)
        {
            // Does this object orbit the sun?
            if (orbit.referenceBody == Planetarium.fetch.Sun)
            {
                numHops = 0;
                return orbit;
            }
            // Does this object orbit something that orbits the sun?
            else if (orbit.referenceBody.GetOrbit().referenceBody == Planetarium.fetch.Sun)
            {
                numHops = 1;
                return orbit.referenceBody.GetOrbit();
            }
            // Does this object orbit the moon of something that orbits the sun?
            else if (orbit.referenceBody.GetOrbit().referenceBody.GetOrbit().referenceBody == Planetarium.fetch.Sun)
            {
                numHops = 2;
                return orbit.referenceBody.GetOrbit().referenceBody.GetOrbit();
            }
            else
            {
                // Nothing in stock KSP orbits more than two levels deep.
                throw new ArgumentException("GetSolarOrbit(): Unable to find a valid solar orbit.");
            }
        }

        // Find the orbits we can use to determine phase angle.  These orbits
        // need to share a common reference body.  We also report how many parent
        // bodies we had to look at to find the returned vesselOrbit, so we can
        // compute the correct ejection angle (either ejection angle, or moon ejection
        // angle for Oberth transfers).
        static private void GetCommonOrbits(ref Orbit vesselOrbit, ref Orbit destinationOrbit, out int vesselOrbitSteps)
        {
            if (vesselOrbit.referenceBody == destinationOrbit.referenceBody)
            {
                // Orbiting the same body.  Easy case.  We're done.
                vesselOrbitSteps = 0;
            }
            else if (vesselOrbit.referenceBody == Planetarium.fetch.Sun)
            {
                // We orbit the sun.  Find the orbit of whichever parent
                // of the target orbits the sun:
                int dontCare;
                destinationOrbit = GetSolarOrbit(destinationOrbit, out dontCare);
                vesselOrbitSteps = 0;
            }
            else if (destinationOrbit.referenceBody == Planetarium.fetch.Sun)
            {
                // The target orbits the sun, but we don't.
                vesselOrbit = GetSolarOrbit(vesselOrbit, out vesselOrbitSteps);
            }
            else
            {
                // Complex case...
                int dontCare;
                Orbit newVesselOrbit = GetSolarOrbit(vesselOrbit, out vesselOrbitSteps);
                Orbit newDestinationOrbit = GetSolarOrbit(destinationOrbit, out dontCare);

                if (newVesselOrbit == newDestinationOrbit)
                {
                    // Even more complex case.  Source and destination orbit are in the
                    // same planetary system, but one or both orbit moons.
                    if (vesselOrbitSteps == 2)
                    {
                        // Vessel orbits a moon.
                        vesselOrbit = vesselOrbit.referenceBody.GetOrbit();
                        vesselOrbitSteps = 1;
                    }
                    if (dontCare == 2)
                    {
                        destinationOrbit = destinationOrbit.referenceBody.GetOrbit();
                    }
                }
                else
                {
                    vesselOrbit = newVesselOrbit;
                    destinationOrbit = newDestinationOrbit;
                }
            }
        }

        // Compute the delta-V required for the injection and circularization burns for the
        // given orbits.
        // 
        // Equation from http://www.braeunig.us/space/
        private void UpdateTransferDeltaV(Orbit startOrbit, Orbit destinationOrbit)
        {
            double GM = startOrbit.referenceBody.gravParameter;
            double rA = startOrbit.semiMajorAxis;
            double rB = destinationOrbit.semiMajorAxis;

            double atx = 0.5 * (rA + rB);
            double Vi = Math.Sqrt(GM / rA); // Velocity of a circular orbit at radius A
            double Vf = Math.Sqrt(GM / rB); // Velocity of a circular orbit at radius B

            double Vtxi = Math.Sqrt(GM * (2.0 / rA - 1.0 / atx));
            double Vtxf = Math.Sqrt(GM * (2.0 / rB - 1.0 / atx));

            initialDeltaV = Vtxi - Vi;
            finalDeltaV = Vf - Vtxf;
        }

        // Computes the delta-V for the initial burn of a Hohmann transfer. Assumes circular
        // initial orbit.
        private static double DeltaVInitial(double startRadius, double endRadius, double GM)
        {
            double atx = 0.5 * (startRadius + endRadius);
            double Vi = Math.Sqrt(GM / startRadius);

            double Vtxi = Math.Sqrt(GM * (2.0 / startRadius - 1.0 / atx));

            return Vtxi - Vi;
        }

        // Allows actual velocity at startRadius to be applied, which may lead to quicker convergence
        // on the desired dV.
        private static double DeltaVInitial(double startRadius, double endRadius, double GM, double velocityAtStartRadius)
        {
            double atx = 0.5 * (startRadius + endRadius);
            //double Vi = Math.Sqrt(GM / startRadius);

            double Vtxi = Math.Sqrt(GM * (2.0 / startRadius - 1.0 / atx));

            return Vtxi - velocityAtStartRadius;
        }

        // Determine the current ejection angle (angle from parent body's prograde)
        private static double ComputeEjectionAngle(Orbit o)
        {
            Vector3d vesselPos = o.pos.xzy;
            vesselPos.Normalize();
            Vector3d bodyProgradeVec = o.referenceBody.orbit.vel.xzy;
            bodyProgradeVec.Normalize();
            Vector3d bodyPosVec = o.referenceBody.orbit.pos;
            double currentEjectionAngle = Vector3d.Angle(vesselPos, bodyProgradeVec);
            if (Vector3d.Dot(vesselPos, bodyPosVec) > 0.0)
            {
                currentEjectionAngle = Utility.NormalizeAngle(360.0 - currentEjectionAngle);
            }
            return currentEjectionAngle;
        }

        // Update ejection parameters
        private static void UpdateEjectionParameters(Orbit o, double departureDeltaV, double currentEjectionAngle, out double ejectionDeltaV, out double transferEjectionAngle, out double timeUntilEjection)
        {
            double r1 = o.semiMajorAxis;
            double r2 = o.referenceBody.sphereOfInfluence;
            double GM = o.referenceBody.gravParameter;
            double v2 = departureDeltaV;

            bool raiseAltitude = (departureDeltaV > 0.0);

            // Absolute velocity required, not delta-V.
            double ejectionVelocity = Math.Sqrt((r1 * (r2 * v2 * v2 - 2.0 * GM) + 2.0 * r2 * GM) / (r1 * r2));

            double eps = ejectionVelocity * ejectionVelocity * 0.5 - GM / r1;
            double h = r1 * ejectionVelocity;
            double e = Math.Sqrt(1.0 + 2.0 * eps * h * h / (GM * GM));
            double theta = Math.Acos(1.0 / e) * Utility.Rad2Deg;
            transferEjectionAngle = Utility.NormalizeAngle(((raiseAltitude) ? 180.0 : 360.0) - theta);

            // Figure out how long until we cross that angle
            double orbitFraction = Utility.NormalizeAngle(currentEjectionAngle - transferEjectionAngle) / 360.0;
            timeUntilEjection = orbitFraction * o.period;

            try
            {
                double oVel = o.getOrbitalSpeedAt(Planetarium.GetUniversalTime() + timeUntilEjection);

                // Convert ejectionVelocity into ejection delta-V.
                ejectionDeltaV = ejectionVelocity - oVel;
            }
            catch
            {
                // Orbit.getOrbitalSpeedAt() can fail for high eccentricity.  Trap those and
                // jam ejectionDeltaV to 0 (we must already be ejecting?).
                ejectionDeltaV = 0.0;
            }
        }

        // Updater method - called at most once per FixedUpdate when the
        // transfer parameters are being queried.
        private void UpdateTransferParameters()
        {
            // Initialize values
            hohmannTransfer = false;
            currentPhaseAngle = 0.0;
            transferPhaseAngle = 0.0;
            timeUntilTransfer = 0.0;

            ejectionDeltaV = 0.0;
            currentEjectionAngle = 0.0;
            transferEjectionAngle = 0.0;
            timeUntilEjection = 0.0;

            oberthAltitude = 0.0;
            oberthEjectionVelocity = 0.0;
            oberthCurrentEjectionAngle = 0.0;
            oberthTransferEjectionAngle = 0.0;
            oberthTimeUntilEjection = 0.0;

            initialDeltaV = 0.0;
            finalDeltaV = 0.0;

            if (vc.activeTarget != null)
            {
                Orbit vesselOrbit = vessel.orbit;
                Orbit destinationOrbit = vc.activeTarget.GetOrbit();

                if (vesselOrbit.eccentricity >= 1.0 || destinationOrbit.eccentricity >= 1.0)
                {
                    // One or both orbits are escape orbits.  We can't work with them.
                    return;
                }

                // Orbit steps counts how many levels of orbital parents we have to step across
                // to find a common orbit with our target.
                // 0 means both orbit the same body (simple Hohmann transfer case).
                // 1 means the vessel orbits a planet, and it must transfer to another planet, 
                //   OR a vessel orbits a moon, and it must transfer to another moon.
                // 2 means the vessel orbits a moon, and it must transfer to another planet.
                int vesselOrbitSteps;
                GetCommonOrbits(ref vesselOrbit, ref destinationOrbit, out vesselOrbitSteps);

                // Figure out what sort of transfer we're doing.
                if (vesselOrbit.referenceBody != destinationOrbit.referenceBody)
                {
                    // We can't find a common orbit?
                    Utility.LogError(this, "Bailing out compute transfer parameters: unable to reconcile orbits");
                    return;
                }

                // TODO: At what relative inclination should it bail out?
                if (Vector3.Angle(vesselOrbit.GetOrbitNormal(), destinationOrbit.GetOrbitNormal()) > 30.0)
                {
                    // Relative inclination is very out-of-spec.  Bail out.
                    return;
                }

                hohmannTransfer = (vessel.orbit.referenceBody == vc.activeTarget.GetOrbit().referenceBody);
                UpdateTransferDeltaV(vesselOrbit, destinationOrbit);

                // transfer phase angle from https://en.wikipedia.org/wiki/Hohmann_transfer_orbit
                // This does not do anything special for Oberth effect transfers
                transferPhaseAngle = 180.0 * (1.0 - 0.35355339 * Math.Pow(vesselOrbit.semiMajorAxis / destinationOrbit.semiMajorAxis + 1.0, 1.5));

#if COMPARE_PHASE_ANGLE_PROTRACTOR
                // current phase angle: the angle between the two positions as projected onto a 2D plane.
                Vector3d pos1 = vesselOrbit.getRelativePositionAtUT(vc.universalTime);
                Vector3d pos2 = destinationOrbit.getRelativePositionAtUT(vc.universalTime);

                double protractorPhaseAngle = ProjectAngle2D(pos1, pos2);
#endif
                // Use orbital parameters.  Note that the argumentOfPeriapsis and LAN
                // are both in degrees, while true anomaly is in radians.
                double tA1 = (vesselOrbit.trueAnomaly * Orbit.Rad2Deg + vesselOrbit.argumentOfPeriapsis + vesselOrbit.LAN);
                double tA2 = (destinationOrbit.trueAnomaly * Orbit.Rad2Deg + destinationOrbit.argumentOfPeriapsis + destinationOrbit.LAN);
                currentPhaseAngle = Utility.NormalizeAngle(tA2 - tA1);

#if COMPARE_PHASE_ANGLE_PROTRACTOR
                if (Math.Abs(currentPhaseAngle - protractorPhaseAngle) > 0.5)
                {
                    Utility.LogMessage(this, "Protractor phase angle = {0,7:0.00}; trueAnomaly pa = {1,7:0.00}, diff = {2,7:0.00}", protractorPhaseAngle, currentPhaseAngle, currentPhaseAngle - protractorPhaseAngle);
                }
#endif

                // The difference in mean motion tells us how quickly the phase angle is changing.
                // Since Orbit.meanMotion is in rad/sec, we need to convert the difference to deg/sec.
                double deltaRelativePhaseAngle = (vesselOrbit.meanMotion - destinationOrbit.meanMotion) * Orbit.Rad2Deg;

                if (deltaRelativePhaseAngle > 0.0)
                {
                    timeUntilTransfer = Utility.NormalizeAngle(currentPhaseAngle - transferPhaseAngle) / deltaRelativePhaseAngle;
                }
                else if (deltaRelativePhaseAngle < 0.0)
                {
                    timeUntilTransfer = Utility.NormalizeAngle(transferPhaseAngle - currentPhaseAngle) / deltaRelativePhaseAngle;
                }
                // else can't compute it - the orbits have the exact same period.  Time already zero'd.

                // Compute current ejection angle
#if COMPARE_PHASE_ANGLE_PROTRACTOR
                    //--- PROTRACTOR
                    Vector3d vesselvec = vessel.orbit.getRelativePositionAtUT(Planetarium.GetUniversalTime());

                    // get planet's position relative to universe
                    Vector3d bodyvec = vessel.mainBody.orbit.getRelativePositionAtUT(Planetarium.GetUniversalTime());

                    Vector3d forwardVec = Quaternion.AngleAxis(90.0f, Vector3d.forward) * bodyvec;
                    forwardVec.Normalize();
                    double protractorEject = Angle2d(vesselvec, Quaternion.AngleAxis(90.0f, Vector3d.forward) * bodyvec);

                    if (Angle2d(vesselvec, Quaternion.AngleAxis(180.0f, Vector3d.forward) * bodyvec) > Angle2d(vesselvec, bodyvec))
                    {
                        protractorEject = 360.0 - protractorEject;//use cross vector to determine up or down
                    }
                    //--- PROTRACTOR
#endif

                if (vesselOrbitSteps == 1)
                {
                    //Vector3d vesselPos = vessel.orbit.pos;
                    //vesselPos.Normalize();
                    //Vector3d bodyProgradeVec = vessel.mainBody.orbit.vel;
                    //bodyProgradeVec.Normalize();
                    //Vector3d bodyPosVec = vessel.mainBody.orbit.pos;
                    //currentEjectionAngle = Vector3d.Angle(vesselPos, bodyProgradeVec);
                    //if (Vector3d.Dot(vesselPos, bodyPosVec) > 0.0)
                    //{
                    //    currentEjectionAngle = Utility.NormalizeAngle(360.0 - currentEjectionAngle);
                    //}

                    currentEjectionAngle = ComputeEjectionAngle(vessel.orbit);
#if COMPARE_PHASE_ANGLE_PROTRACTOR
                    Utility.LogMessage(this, "Protractor ejection angle = {0,5:0.0} , computed = {1,5:0.0}", protractorEject, currentEjectionAngle);
#endif

                    UpdateEjectionParameters(vessel.orbit, initialDeltaV, currentEjectionAngle, out ejectionDeltaV, out transferEjectionAngle, out timeUntilEjection);
                }
                else if (vesselOrbitSteps == 2)
                {
                    CelestialBody moon = vessel.orbit.referenceBody;
                    CelestialBody planet = moon.referenceBody;

                    currentEjectionAngle = ComputeEjectionAngle(moon.orbit);

                    // Compute ejection parameters based on the orbital parameters of the moon we are orbiting.
                    UpdateEjectionParameters(moon.orbit, initialDeltaV, currentEjectionAngle, out ejectionDeltaV, out transferEjectionAngle, out timeUntilEjection);

                    // TODO: Compute moon ejection angle to take advantage of the Oberth effect.
                    oberthAltitude = 0.05 * (planet.Radius + planet.atmosphereDepth);

                    double oberthAltitudeDeltaV = DeltaVInitial(moon.orbit.semiMajorAxis, oberthAltitude + planet.Radius, planet.gravParameter);

                    // Compute the moon-specific parameters
                    oberthCurrentEjectionAngle = ComputeEjectionAngle(vessel.orbit);
                    UpdateEjectionParameters(vessel.orbit, oberthAltitudeDeltaV, oberthCurrentEjectionAngle, out oberthEjectionVelocity, out oberthTransferEjectionAngle, out oberthTimeUntilEjection);

                    // Delta-V required to target the ejection altitude.
                    //oberthEjectionVelocity = 0.0;
                }
            }

            invalid = false;
        }

        /// <summary>
        /// Called per-FixedUpdate to invalidate computations.
        /// </summary>
        [MoonSharpHidden]
        internal void Update()
        {
            invalid = true;
        }
    }
}
