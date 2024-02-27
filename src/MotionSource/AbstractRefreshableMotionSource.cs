using SimpleJSON;
using ToySerialController.UI;
using ToySerialController.Utils;
using UnityEngine;

namespace ToySerialController.MotionSource
{
    public abstract class AbstractRefreshableMotionSource : IMotionSource
    {
        private UIDynamicButton RefreshButton;
        private UIDynamic Spacer;

        private JSONStorableAction RefreshAction;

        public abstract Vector3 ReferencePosition { get; }
        public abstract Vector3 ReferencePositionRaw { get; }
        public abstract Vector3 ReferenceUp { get; }
        public abstract Vector3 ReferenceRight { get; }
        public abstract Vector3 ReferenceForward { get; }
        public abstract float ReferenceLength { get; }
        public abstract float RealReferenceLength { get; }
        public abstract float ReferenceRadius { get; }
        public abstract Vector3 ReferencePlaneNormal { get; }
        public abstract Vector3 TargetPosition { get; }
        public abstract Vector3 TargetUp { get; }
        public abstract Vector3 TargetRight { get; }
        public abstract Vector3 TargetForward { get; }

        public abstract bool Update();
        public abstract void StoreConfig(JSONNode config);
        public abstract void RestoreConfig(JSONNode config);
        public abstract float GetReferenceLength();
        protected abstract void _SetBaseOffset(float offset);
        protected abstract float _GetBaseOffset();

        public virtual void CreateUI(IUIBuilder builder)
        {
            RefreshButton = builder.CreateButton("Refresh", () =>
            {
                ComponentCache.Clear();
                RefreshButtonCallback();
            });
            RefreshButton.buttonColor = new Color(0, 0.75f, 1f) * 0.8f;
            RefreshButton.textColor = Color.white;

            Spacer = builder.CreateSpacer(200);

            RefreshAction = UIManager.CreateAction("Refresh Motion Source", () =>
            {
                ComponentCache.Clear();
                RefreshButtonCallback();
            });
        }

        public virtual void DestroyUI(IUIBuilder builder)
        {
            builder.Destroy(RefreshButton);
            builder.Destroy(Spacer);

            UIManager.RemoveAction(RefreshAction);
        }

        protected abstract void RefreshButtonCallback();

        public virtual void OnSceneChanging() { }
        public virtual void OnSceneChanged()
        {
            RefreshButtonCallback();
        }

        public float GetRealReferenceLength()
        {
            return GetReferenceLength();
        }

        public void SetBaseOffset(float offset)
        {
            // call abstract function
            _SetBaseOffset(offset);
        }

        public float GetBaseOffset()
        {
            // call abstract function
            return _GetBaseOffset();
        }
    }
}
