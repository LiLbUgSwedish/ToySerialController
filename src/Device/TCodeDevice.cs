using DebugUtils;
using System.Linq;
using System.Text;
using ToySerialController.MotionSource;
using ToySerialController.Device.OutputTarget;
using UnityEngine;
using ToySerialController.Utils;
using ToySerialController.UI;
using System.Collections.Generic;
using System;

namespace ToySerialController
{
    public partial class TCodeDevice : IDevice
    {
        protected readonly float[] XTarget, XTargetRaw, RTarget, ETarget;
        protected readonly float[] XCmd, RCmd, ECmd;
        protected readonly float[] LastXCmd, LastRCmd, LastECmd;
        protected readonly JSONStorableFloat[] XParam, RParam, EParam;

        private readonly StringBuilder _stringBuilder;
        private readonly StringBuilder _deviceReportBuilder;

        private float? _lastNoCollisionTime;
        private bool _lastNoCollisionSmoothingEnabled;
        private float _lastCollisionSmoothingT;
        private float _lastNoCollisionSmoothingStartTime, _lastNoCollisionSmoothingDuration;
        private bool _isLoading;

        // TODO:: remember the maximum and minimum L0 values
        // we can try to use this, along with reference length and base offset maybe, to try to auto-configure.
        private float _minL0 = 1.0f;
        private float _maxL0 = 0.0f;
        // keep track of the last peak and last trough
        private DateFloat _lastPeak = new DateFloat(DateTime.Now, 1);
        private DateFloat _lastTrough = new DateFloat(DateTime.Now, 0);
        // remember if we are not in contact with penis, then we can return placeholder values.
        private bool _aboveTarget = false;

        // keep a list of L0 values we can use to check min and max values for; truncate after a certain amount of time.
        private List<DateFloat> _l0Values = new List<DateFloat>();

        // remember the male penis length, used to calculate base offset?
        private float _length = 0;

        public class DateFloat {
            public DateTime Date;
            public float Value;

            public DateFloat(DateTime date, float val)
            {
                Date = date;
                Value = val;
            }
        }

        public string GetDeviceReport()
        {
            const string format = "{0,5:0.00},\t";

            _deviceReportBuilder.Length = 0;
            return _deviceReportBuilder.Append("    Target    Cmd    Output\n")
                .Append("L0\t").AppendFormat(format, XTarget[0]).AppendFormat(format, XCmd[0]).AppendTCode("L0", XCmd[0]).AppendLine()
                .Append("L1\t").AppendFormat(format, XTarget[1]).AppendFormat(format, XCmd[1]).AppendTCode("L1", XCmd[1]).AppendLine()
                .Append("L2\t").AppendFormat(format, XTarget[2]).AppendFormat(format, XCmd[2]).AppendTCode("L2", XCmd[2]).AppendLine()
                .Append("R0\t").AppendFormat(format, RTarget[0]).AppendFormat(format, RCmd[0]).AppendTCode("R0", RCmd[0]).AppendLine()
                .Append("R1\t").AppendFormat(format, RTarget[1]).AppendFormat(format, RCmd[1]).AppendTCode("R1", RCmd[1]).AppendLine()
                .Append("R2\t").AppendFormat(format, RTarget[2]).AppendFormat(format, RCmd[2]).AppendTCode("R2", RCmd[2]).AppendLine()
                .Append("V0\t").AppendFormat(format, ETarget[0]).AppendFormat(format, ECmd[0]).AppendTCode("V0", ECmd[0]).AppendLine()
                .Append("A0\t").AppendFormat(format, ETarget[1]).AppendFormat(format, ECmd[1]).AppendTCode("A0", ECmd[1]).AppendLine()
                .Append("A1\t").AppendFormat(format, ETarget[2]).AppendFormat(format, ECmd[2]).AppendTCode("A1", ECmd[2]).AppendLine()
                .Append("A2\t").AppendFormat(format, ETarget[3]).AppendFormat(format, ECmd[3]).AppendTCode("A2", ECmd[3])
                .ToString();
        }
        public string GetL0Report()
        {
            return "Max L0: " + _maxL0 + "\r\nMin L0: " + _minL0;
        }

        public string GetConfigCalc()
        {
            // determine recommended length
            var length = InflateLength();
            // determine recommended base offset
            var offset = InflateOffset();
            return "Recommended Settings: \r\n  Base Offset: " + offset + "\r\nReference Length: " + length;
        }

        // get the recommended length based on the readings we have
        private float GetRecommendedLength()
        {
            // if we are above target, recommended length is always 100% (so we get good feedback when inserting penis)
            if (_aboveTarget)
            {
                return 1;
            }

            // calculate the recommended length based on how much of the actual penis length is utilized
            float recommend = (_maxL0 - _minL0);
            // now compensate for the fact that the base offset (once applied) will effectively shorten the penis so we need to buff the length in return
            // NOTE:: we * by negative 1 since a negative base offset will DECREASE the available length
            float offsetAsPercLength = GetRecommendedOffset() / _length * -1; // the percentage of the length we used up on the base offset
            
            // increase the length by the (ratio?) of the full length to the length left over after changing the base
            recommend = recommend / (1 - offsetAsPercLength);

            // NOTE:: the recommended length is a PERCENTAGE. We set this directly onto the ReferenceLength slider, which is the percentage of the actual length to use.
            return recommend;
        }

        // get the recommended offset based on the readings we have
        private float GetRecommendedOffset()
        {
            // if we are above target, recommended offset is always 0 (so we have a good range of motion for penis insertion)
            if (_aboveTarget)
            {
                return 0;
            }

            // determine the desired offset (this is in UNITS not percent, so multiple by the length to get the actual offset)
            // we do 1 - maxL0 to get the percentage of the available length that is BELOW the deepest point reached (remember L0 axis is inverted)
            return ((1 - _maxL0) * _length) * -1;
        }

        private float InflateLength()
        {
            // inflate the length by the configured amount
            float inflate = AutoConfigBuffer.val + 1;
            return GetRecommendedLength() * inflate;
        }

        private float InflateOffset()
        {
            // reduce or increase the base offset by half the amount we changed the length by
            // NOTE we need to convert the length to units rather than percentage
            float inflate = (AutoConfigBuffer.val / 2) * (GetRecommendedLength() * _length);
            // add this to the offset (if offset is negative and inflation is positive, the base offset will move TOWARD the pelvis, which is what we want).
            return GetRecommendedOffset() + inflate;
        }

        public void ResetCounters()
        {
            _maxL0 = 0.0f;
            _minL0 = 1.0f;
            _lastPeak = new DateFloat(DateTime.Now, 1);
            _lastTrough = new DateFloat(DateTime.Now, 0);
            _l0Values.Clear();
        }

        public TCodeDevice()
        {
            XTarget = new float[3];
            XTargetRaw = new float[1];
            RTarget = new float[3];
            ETarget = new float[4];

            XCmd = Enumerable.Repeat(0.5f, XTarget.Length).ToArray();
            RCmd = Enumerable.Repeat(0.5f, RTarget.Length).ToArray();
            ECmd = Enumerable.Repeat(0f, ETarget.Length).ToArray();

            LastXCmd = Enumerable.Repeat(float.NaN, XCmd.Length).ToArray();
            LastRCmd = Enumerable.Repeat(float.NaN, RCmd.Length).ToArray();
            LastECmd = Enumerable.Repeat(float.NaN, ECmd.Length).ToArray();

            XParam = new JSONStorableFloat[XCmd.Length];
            RParam = new JSONStorableFloat[RCmd.Length];
            EParam = new JSONStorableFloat[ECmd.Length];

            for (var i = 0; i < XCmd.Length; i++)
                XParam[i] = UIManager.CreateFloat($"Device:L{i}:Value", XCmd[i], 0, 1);
            for (var i = 0; i < RCmd.Length; i++)
                RParam[i] = UIManager.CreateFloat($"Device:R{i}:Value", RCmd[i], 0, 1);

            var eNames = new string[] { "V0", "A0", "A1", "A2" };
            for(var i = 0; i < eNames.Length; i++)
                EParam[i] = UIManager.CreateFloat($"Device:{eNames[i]}:Value", ECmd[i], 0, 1);

            _lastNoCollisionTime = Time.time;
            _stringBuilder = new StringBuilder();
            _deviceReportBuilder = new StringBuilder();
        }

        private static void AppendIfChanged(StringBuilder stringBuilder, string axisName, float cmd, float lastCmd)
        {
            if (float.IsNaN(lastCmd) || Mathf.Abs(lastCmd - cmd) * 9999 >= 1)
                stringBuilder.AppendTCode(axisName, cmd).Append(" ");
        }

        public bool Update(IMotionSource motionSource, IOutputTarget outputTarget, IDeviceRecorder recorder)
        {
            if (_isLoading)
            {
                for (var i = 0; i < ETarget.Length; i++) ETarget[i] = Mathf.Lerp(ETarget[i], 0f, 0.05f);
                for (var i = 0; i < XTarget.Length; i++) XTarget[i] = Mathf.Lerp(XTarget[i], 0.5f, 0.05f);
                for (var i = 0; i < RTarget.Length; i++) RTarget[i] = Mathf.Lerp(RTarget[i], 0f, 0.05f);
            }
            else if (motionSource != null)
            {
                UpdateMotion(motionSource);
                // update the tracking values we use for auto-adjusting the penis base and reference length
                UpdateAutoConfig(motionSource);
                UpdateConfig(motionSource);

                DebugDraw.DrawCircle(motionSource.TargetPosition + motionSource.TargetUp * RangeMinL0Slider.val * motionSource.ReferenceLength, motionSource.TargetUp, motionSource.TargetRight, Color.white, 0.05f);
                DebugDraw.DrawCircle(motionSource.TargetPosition + motionSource.TargetUp * RangeMaxL0Slider.val * motionSource.ReferenceLength, motionSource.TargetUp, motionSource.TargetRight, Color.white, 0.05f);
            }

            UpdateValues(outputTarget);

            recorder?.RecordValues(Time.time, XCmd[0], XCmd[1], XCmd[2], RCmd[0], RCmd[1], RCmd[2]);
            return true;
        }

        public void UpdateValues(IOutputTarget outputTarget)
        {
            if (_lastNoCollisionSmoothingEnabled)
            {
                _lastCollisionSmoothingT = Mathf.Pow(2, 10 * ((Time.time - _lastNoCollisionSmoothingStartTime) / _lastNoCollisionSmoothingDuration - 1));
                if (_lastCollisionSmoothingT > 1.0f)
                {
                    _lastNoCollisionSmoothingEnabled = false;
                    _lastCollisionSmoothingT = 0;
                }
            }

            UpdateL0(); UpdateL1(); UpdateL2();
            UpdateR0(); UpdateR1(); UpdateR2();
            UpdateV0();
            UpdateA0(); UpdateA1(); UpdateA2();

            _stringBuilder.Length = 0;
            AppendIfChanged(_stringBuilder, "L0", XCmd[0], LastXCmd[0]);
            AppendIfChanged(_stringBuilder, "L1", XCmd[1], LastXCmd[1]);
            AppendIfChanged(_stringBuilder, "L2", XCmd[2], LastXCmd[2]);
            AppendIfChanged(_stringBuilder, "R0", RCmd[0], LastRCmd[0]);
            AppendIfChanged(_stringBuilder, "R1", RCmd[1], LastRCmd[1]);
            AppendIfChanged(_stringBuilder, "R2", RCmd[2], LastRCmd[2]);
            AppendIfChanged(_stringBuilder, "V0", ECmd[0], LastECmd[0]);
            AppendIfChanged(_stringBuilder, "A0", ECmd[1], LastECmd[1]);
            AppendIfChanged(_stringBuilder, "A1", ECmd[2], LastECmd[2]);
            AppendIfChanged(_stringBuilder, "A2", ECmd[3], LastECmd[3]);

            XCmd.CopyTo(LastXCmd, 0);
            RCmd.CopyTo(LastRCmd, 0);
            ECmd.CopyTo(LastECmd, 0);

            for (var i = 0; i < XCmd.Length; i++) XParam[i].valNoCallback = XCmd[i];
            for (var i = 0; i < RCmd.Length; i++) RParam[i].valNoCallback = RCmd[i];
            for (var i = 0; i < ECmd.Length; i++) EParam[i].valNoCallback = ECmd[i];

            if (_stringBuilder.Length > 0)
                outputTarget?.Write(_stringBuilder.AppendLine().ToString());
        }

        public void UpdateL0()
        {
            var t = Mathf.Clamp01((XTarget[0] - RangeMinL0Slider.val) / (RangeMaxL0Slider.val - RangeMinL0Slider.val));
            var output = Mathf.Clamp01(Mathf.Lerp(OutputMinL0Slider.val, OutputMaxL0Slider.val, t));

            if (InvertL0Toggle.val) output = 1f - output;
            if (EnableOverrideL0Toggle.val) output = OverrideL0Slider.val;
            if (_lastNoCollisionSmoothingEnabled)
                output = Mathf.Lerp(XCmd[0], output, _lastCollisionSmoothingT);

            XCmd[0] = Mathf.Lerp(XCmd[0], output, 1 - SmoothingSlider.val);
        }

        public void UpdateL1()
        {
            var t = Mathf.Clamp01((XTarget[1] + RangeMaxL1Slider.val) / (2 * RangeMaxL1Slider.val));
            var output = Mathf.Clamp01(OffsetL1Slider.val + 0.5f + Mathf.Lerp(-OutputMaxL1Slider.val, OutputMaxL1Slider.val, t));

            if (InvertL1Toggle.val) output = 1f - output;
            if (EnableOverrideL1Toggle.val) output = OverrideL1Slider.val;
            if (_lastNoCollisionSmoothingEnabled)
                output = Mathf.Lerp(XCmd[1], output, _lastCollisionSmoothingT);

            XCmd[1] = Mathf.Lerp(XCmd[1], output, 1 - SmoothingSlider.val);
        }

        public void UpdateL2()
        {
            var t = Mathf.Clamp01((XTarget[2] + RangeMaxL2Slider.val) / (2 * RangeMaxL2Slider.val));
            var output = Mathf.Clamp01(OffsetL2Slider.val + 0.5f + Mathf.Lerp(-OutputMaxL2Slider.val, OutputMaxL2Slider.val, t));

            if (InvertL2Toggle.val) output = 1f - output;
            if (EnableOverrideL2Toggle.val) output = OverrideL2Slider.val;
            if (_lastNoCollisionSmoothingEnabled)
                output = Mathf.Lerp(XCmd[2], output, _lastCollisionSmoothingT);

            XCmd[2] = Mathf.Lerp(XCmd[2], output, 1 - SmoothingSlider.val);
        }

        public void UpdateR0()
        {
            var t = Mathf.Clamp01(0.5f + (RTarget[0] / 2) / (RangeMaxR0Slider.val / 180));
            var output = Mathf.Clamp01(OffsetR0Slider.val + 0.5f + Mathf.Lerp(-OutputMaxR0Slider.val, OutputMaxR0Slider.val, t));

            if (InvertR0Toggle.val) output = 1f - output;
            if (EnableOverrideR0Toggle.val) output = OverrideR0Slider.val;
            if (EnableThrustSyncR0Toggle.val) output = XCmd[0];
            if (_lastNoCollisionSmoothingEnabled)
                output = Mathf.Lerp(RCmd[0], output, _lastCollisionSmoothingT);

            RCmd[0] = Mathf.Lerp(RCmd[0], output, 1 - SmoothingSlider.val);
        }

        public void UpdateR1()
        {
            var t = Mathf.Clamp01(0.5f + (RTarget[1] / 2) / (RangeMaxR1Slider.val / 90));
            var output = Mathf.Clamp01(OffsetR1Slider.val + 0.5f + Mathf.Lerp(-OutputMaxR1Slider.val, OutputMaxR1Slider.val, t));

            if (InvertR1Toggle.val) output = 1f - output;
            if (EnableOverrideR1Toggle.val) output = OverrideR1Slider.val;
            if (_lastNoCollisionSmoothingEnabled)
                output = Mathf.Lerp(RCmd[1], output, _lastCollisionSmoothingT);

            RCmd[1] = Mathf.Lerp(RCmd[1], output, 1 - SmoothingSlider.val);
        }

        public void UpdateR2()
        {
            var t = Mathf.Clamp01(0.5f + (RTarget[2] / 2) / (RangeMaxR2Slider.val / 90));
            var output = Mathf.Clamp01(OffsetR2Slider.val + 0.5f + Mathf.Lerp(-OutputMaxR2Slider.val, OutputMaxR2Slider.val, t));

            if (InvertR2Toggle.val) output = 1f - output;
            if (EnableOverrideR2Toggle.val) output = OverrideR2Slider.val;
            if (_lastNoCollisionSmoothingEnabled)
                output = Mathf.Lerp(RCmd[2], output, _lastCollisionSmoothingT);

            RCmd[2] = Mathf.Lerp(RCmd[2], output, 1 - SmoothingSlider.val);
        }

        public void UpdateV0()
        {
            var output = Mathf.Clamp01(ETarget[0]);

            if (EnableOverrideV0Toggle.val) output = OverrideV0Slider.val;
            if (_lastNoCollisionSmoothingEnabled)
                output = Mathf.Lerp(ECmd[0], output, _lastCollisionSmoothingT);

            ECmd[0] = Mathf.Lerp(ECmd[0], output, 1 - SmoothingSlider.val);
        }

        public void UpdateA0()
        {
            var output = Mathf.Clamp01(ETarget[1]);

            if (EnableOverrideA0Toggle.val) output = OverrideA0Slider.val;
            if (_lastNoCollisionSmoothingEnabled)
                output = Mathf.Lerp(ECmd[1], output, _lastCollisionSmoothingT);

            ECmd[1] = Mathf.Lerp(ECmd[1], output, 1 - SmoothingSlider.val);
        }

        public void UpdateA1()
        {
            var output = Mathf.Clamp01(ETarget[2]);

            if (EnableOverrideA1Toggle.val) output = OverrideA1Slider.val;
            if (_lastNoCollisionSmoothingEnabled)
                output = Mathf.Lerp(ECmd[2], output, _lastCollisionSmoothingT);

            ECmd[2] = Mathf.Lerp(ECmd[2], output, 1 - SmoothingSlider.val);
        }

        public void UpdateA2()
        {
            var output = Mathf.Clamp01(ETarget[3]);

            if (EnableOverrideA2Toggle.val) output = OverrideA2Slider.val;
            if (_lastNoCollisionSmoothingEnabled)
                output = Mathf.Lerp(ECmd[3], output, _lastCollisionSmoothingT);

            ECmd[3] = Mathf.Lerp(ECmd[3], output, 1 - SmoothingSlider.val);
        }

        public bool UpdateMotion(IMotionSource motionSource)
        {
            var length = motionSource.ReferenceLength * ReferenceLengthScaleSlider.val;
            var radius = motionSource.ReferenceRadius * ReferenceRadiusScaleSlider.val;
            var referenceEnding = motionSource.ReferencePosition + motionSource.ReferenceUp * length;
            var diffPosition = motionSource.TargetPosition - motionSource.ReferencePosition;

            for (var i = 0; i < 5; i++)
                DebugDraw.DrawCircle(Vector3.Lerp(motionSource.ReferencePosition, referenceEnding, i / 4.0f), motionSource.ReferenceUp, motionSource.ReferenceRight, Color.grey, radius);

            var t = Vector3.Dot(motionSource.TargetPosition - motionSource.ReferencePosition, motionSource.ReferenceUp);
            var closestPoint = motionSource.ReferencePosition + motionSource.ReferenceUp * t;

            if (Vector3.Magnitude(closestPoint - motionSource.TargetPosition) <= radius)
            {
                if (diffPosition.magnitude > 0.0001f)
                {
                    XTarget[0] = 1 - (closestPoint - motionSource.ReferencePosition).magnitude / length;

                    var diffOnPlane = Vector3.ProjectOnPlane(diffPosition, motionSource.ReferencePlaneNormal);
                    var rightOffset = Vector3.Project(diffOnPlane, motionSource.ReferenceRight);
                    var forwardOffset = Vector3.Project(diffOnPlane, motionSource.ReferenceForward);
                    XTarget[1] = forwardOffset.magnitude * Mathf.Sign(Vector3.Dot(forwardOffset, motionSource.ReferenceForward));
                    XTarget[2] = rightOffset.magnitude * Mathf.Sign(Vector3.Dot(rightOffset, motionSource.ReferenceRight));
                }
                else
                {
                    XTarget[0] = 1;
                    XTarget[1] = 0;
                    XTarget[2] = 0;
                }

                var correctedRight = Vector3.ProjectOnPlane(motionSource.TargetRight, motionSource.ReferenceUp);
                if (Vector3.Dot(correctedRight, motionSource.ReferenceRight) < 0)
                    correctedRight -= 2 * Vector3.Project(correctedRight, motionSource.ReferenceRight);

                RTarget[0] = Vector3.SignedAngle(motionSource.ReferenceRight, correctedRight, motionSource.ReferenceUp) / 180;
                RTarget[1] = -Vector3.SignedAngle(motionSource.ReferenceUp, Vector3.ProjectOnPlane(motionSource.TargetUp, motionSource.ReferenceForward), motionSource.ReferenceForward) / 90;
                RTarget[2] = Vector3.SignedAngle(motionSource.ReferenceUp, Vector3.ProjectOnPlane(motionSource.TargetUp, motionSource.ReferenceRight), motionSource.ReferenceRight) / 90;

                ETarget[0] = OutputV0CurveEditorSettings.Evaluate(XTarget, RTarget);
                ETarget[1] = OutputA0CurveEditorSettings.Evaluate(XTarget, RTarget);
                ETarget[2] = OutputA1CurveEditorSettings.Evaluate(XTarget, RTarget);
                ETarget[3] = OutputA2CurveEditorSettings.Evaluate(XTarget, RTarget);

                if (_lastNoCollisionTime != null)
                {
                    _lastNoCollisionSmoothingEnabled = true;
                    _lastNoCollisionSmoothingStartTime = Time.time;
                    _lastNoCollisionSmoothingDuration = Mathf.Clamp(Time.time - _lastNoCollisionTime.Value, 0.5f, 2);
                    _lastNoCollisionTime = null;
                }

                return true;
            }
            else
            {
                if (_lastNoCollisionTime == null)
                    _lastNoCollisionTime = Time.time;

                return false;
            }
        }

        private bool UpdateAutoConfig(IMotionSource motionSource)
        {
            if (AutoConfigToggle.val)
            {
                var length = motionSource.GetRealReferenceLength();
                _length = motionSource.GetRealReferenceLength();
                var radius = motionSource.ReferenceRadius;
                // get the raw reference position (ignoring penis base offset or other settings)
                var position = motionSource.ReferencePositionRaw;
                var referenceEnding = position + motionSource.ReferenceUp * length;
                var diffPosition = motionSource.TargetPosition - position;
                var diffEnding = motionSource.TargetPosition - referenceEnding;
                var aboveTarget = (Vector3.Dot(diffPosition, motionSource.TargetUp) < 0 && Vector3.Dot(diffEnding, motionSource.TargetUp) < 0)
                                    || Vector3.Dot(diffPosition, motionSource.ReferenceUp) < 0;
                _aboveTarget = aboveTarget;
                var t = Vector3.Dot(motionSource.TargetPosition - position, motionSource.ReferenceUp);
                var closestPoint = position + motionSource.ReferenceUp * t;
                if (Vector3.Magnitude(closestPoint - motionSource.TargetPosition) <= radius)
                {
                    if (diffPosition.magnitude > 0.0001f)
                    {
                        XTargetRaw[0] = 1 - ((closestPoint - position).magnitude / length);
                        if (aboveTarget)
                            XTargetRaw[0] = XTargetRaw[0] > 0 ? 1 : 0;
                    }
                    else
                    {
                        XTargetRaw[0] = 1;
                    }

                    // update min and max
                    if (AutoStyleChooser.val == "Min + Max")
                    {
                        _minL0 = _minL0 < XTargetRaw[0] ? _minL0 : XTargetRaw[0];
                        _maxL0 = _maxL0 > XTargetRaw[0] ? _maxL0 : XTargetRaw[0];
                    }
                    if (AutoStyleChooser.val == "Average over time")
                    {
                        _l0Values[DateTime.Now] = XTargetRaw[0];
                        // remove any old timestamps
                        for (int i = 0; i < _l0Values.Count; i++)
                        {
                            var item = _l0Values.ElementAt(i);
                            if (DateTime.Now.Subtract(item.Key).TotalSeconds > AutoConfigBufferLength.val)
                            {
                                _l0Values.Remove(item.Key);
                                i--;
                            }
                        }
                        // determine min and max from history
                        _minL0 = _l0Values.Min(x => x.Value);
                        _maxL0 = _l0Values.Max(x => x.Value);
                    }
                    // look for peaks or trouphs
                    if (AutoStyleChooser.val == "Recent Peak & Trough")
                    {
                        _l0Values.Add(new DateFloat(DateTime.Now, XTargetRaw[0]));
                        if (_l0Values.Count >= 7)
                        {
                            if (_l0Values.Count > 7)
                            {
                                // truncate to 7, thats all we need
                                _l0Values.RemoveRange(0, _l0Values.Count - 7);
                            }
                            // get the last, second last, and third last recorded thrust values
                            DateFloat last1 = _l0Values[_l0Values.Count - 1];
                            DateFloat last2 = _l0Values[_l0Values.Count - 2];
                            DateFloat last3 = _l0Values[_l0Values.Count - 3];
                            DateFloat last4 = _l0Values[_l0Values.Count - 4];
                            DateFloat last5 = _l0Values[_l0Values.Count - 5];
                            DateFloat last6 = _l0Values[_l0Values.Count - 6];
                            DateFloat last7 = _l0Values[_l0Values.Count - 7];
                            // if the current value is greater than the last trough, just use that value immediately
                            if (last1.Value > _lastTrough.Value)
                            {
                                _lastTrough = last1;
                                //SuperController.LogMessage($"Force TROUGH");
                            }
                            // else do some checks if the middle value is greater than both surrounding values, indicating it is some kind of peak
                            else if (last1.Value < last4.Value && last2.Value < last4.Value && last3.Value < last4.Value && last5.Value < last4.Value && last6.Value < last4.Value && last7.Value < last4.Value)
                            {
                                // if this trough is very close to the previous peak in TIME or SPACE, ignore it (we can wobble a bit at either peak or trough and need to ignore it)
                                if (last4.Date.Subtract(_lastPeak.Date).TotalMilliseconds > 100 && Math.Abs(_lastPeak.Value - last4.Value) > 0.05f)
                                {
                                    // we are not close to the last peak, lets check if we're close to the last trough in TIME
                                    if (last4.Date.Subtract(_lastTrough.Date).TotalMilliseconds < 100)
                                    {
                                        // we are close to the previous trough, so only keep the larger of the two
                                        if (_lastTrough.Value < last4.Value)
                                        {
                                            _lastTrough = last4;
                                            //SuperController.LogMessage($"TROUGH UPDATE");
                                        }
                                    }
                                    else
                                    {
                                        // this is a new trough so just keep it
                                        _lastTrough = last4;
                                        //SuperController.LogMessage($"TROUGH");

                                    }
                                }
                            }
                            // if the current value is less than the last peak, just use that value immediately
                            if (last1.Value < _lastPeak.Value)
                            {
                                _lastPeak = last1;
                                //SuperController.LogMessage($"Force PEAK");
                            }
                            // else do some checks if the middle value is greater than both surrounding values, indicating it is some kind of peak
                            else if (last1.Value > last4.Value && last2.Value > last4.Value && last3.Value > last4.Value && last5.Value > last4.Value && last6.Value > last4.Value && last7.Value > last4.Value)
                            {
                                // if this peak is very close to the previous trough in TIME or SPACE, ignore it (we can wobble a bit at either peak or trough and need to ignore it)
                                if (last4.Date.Subtract(_lastTrough.Date).TotalMilliseconds > 100 && Math.Abs(_lastTrough.Value - last4.Value) > 0.05f)
                                {
                                    // we are not close to the last peak, lets check if we're close to the last peak in TIME
                                    if (last2.Date.Subtract(_lastPeak.Date).TotalMilliseconds < 100)
                                    {
                                        // we are close to the previous peak, so only keep the smaller of the two
                                        if (_lastPeak.Value > last4.Value)
                                        {
                                            _lastPeak = last4;
                                            //SuperController.LogMessage($"PEAK UPDATE");
                                        }
                                    }
                                    else
                                    {
                                        // this is a new peak so just keep it
                                        _lastPeak = last4;
                                        //SuperController.LogMessage($"PEAK");
                                    }
                                }
                            }

                            // update max values
                            _maxL0 = _lastTrough.Value;
                            _minL0 = _lastPeak.Value;
                        }
                    }


                    return true;
                }
                else
                {
                    return false;
                }
            }
            return false;
        }

        private bool UpdateConfig(IMotionSource motionSource)
        {
            // update the length
            if (AutoConfigToggle.val)
            {
                // determine proposed length
                var length = InflateLength();
                // determine length error
                var lengthDelta = length - ReferenceLengthScaleSlider.val;
                //SuperController.LogMessage($"length: {length} lengthD: {lengthDelta} XTarget: {XTarget[0]} Reference: {ReferenceLengthScaleSlider.val}");

                // if the current length is deemed too short, lengthen, but only if current target is below 5% (penis is JUST inside vagina, about to pop out? so we can lengthen to avoid or re-insert?)
                //if (lengthDelta > 0 && XTarget[0] < 0.05)
                if (lengthDelta > 0.005)
                {
                    // NOTE:: We do not allow lengthening when ?? not sure but it works, keep an eye on it
                    ReferenceLengthScaleSlider.val += 0.01f;
                    //SuperController.LogMessage("Lengthen");
                }

                // if the current length is deemed too long, shorten, but only if current target is above 5% (penis is inside vagina, if < 5% then penis is outside vagina)
                //if (lengthDelta > 0 && XTarget[0] > 0.05 && ReferenceLengthScaleSlider.val > 0.1)
                if (lengthDelta < -0.005 && ReferenceLengthScaleSlider.val > 0.1)
                {
                    // NOTE:: We do not allow shortening when ?? not sure but it works, keep an eye on it
                    ReferenceLengthScaleSlider.val -= 0.01f;
                    //SuperController.LogMessage("Shorten");
                }

                // determine proposed base offset
                var offset = InflateOffset();
                // round to be less precise
                offset = Mathf.Round(offset * 1000f) / 1000f;
                //SuperController.LogMessage($"offset: {offset} BaseOffset: {motionSource.PenisBaseOffset}");

                // if current base is deemed too shallow, extend, but only if ??
                //if (offset < motionSource.GetBaseOffset() && XTarget[0] < 0.95)
                if (offset < (motionSource.GetBaseOffset() - 0.0005))
                {
                    //motionSource.PenisBaseOffset -= 0.001f;
                    motionSource.SetBaseOffset(motionSource.GetBaseOffset() - 0.001f);
                }
                // if current base is deemed too deep, retract, but only if ??
                //if (offset > motionSource.GetBaseOffset() && XTarget[0] > 0.95)
                if (offset > (motionSource.GetBaseOffset() + 0.0005))
                {
                    //motionSource.PenisBaseOffset += 0.001f;
                    motionSource.SetBaseOffset(motionSource.GetBaseOffset() + 0.001f);
                }
                return true;
            }
            return false;
        }

        public void Dispose() => Dispose(true);
        protected virtual void Dispose(bool disposing)
        {
            foreach (var storable in XParam)
                UIManager.RemoveFloat(storable);
            foreach (var storable in RParam)
                UIManager.RemoveFloat(storable);
            foreach (var storable in EParam)
                UIManager.RemoveFloat(storable);
        }

        public virtual void OnSceneChanging() => _isLoading = true;
        public virtual void OnSceneChanged()
        {
            _lastNoCollisionSmoothingEnabled = true;
            _lastNoCollisionSmoothingStartTime = Time.time;
            _lastNoCollisionSmoothingDuration = 2;
            _lastNoCollisionTime = null;

            _isLoading = false;
        }
    }
}
