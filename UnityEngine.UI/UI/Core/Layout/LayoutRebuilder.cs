using System.Collections.Generic;
using UnityEngine.Events;

namespace UnityEngine.UI
{
    public class LayoutRebuilder : ICanvasElement
    {
        private RectTransform m_ToRebuild; //包含(ILayoutController组件)
        //There are a few of reasons we need to cache the Hash fromt he transform:
        //  - This is a ValueType (struct) and .Net calculates Hash from the Value Type fields.
        //  - The key of a Dictionary should have a constant Hash value.
        //  - It's possible for the Transform to get nulled from the Native side.
        // We use this struct with the IndexedSet container, which uses a dictionary as part of it's implementation
        // So this struct gets used as a key to a dictionary, so we need to guarantee a constant Hash value.
        private int m_CachedHashFromTransform;

        static ObjectPool<LayoutRebuilder> s_Rebuilders = new ObjectPool<LayoutRebuilder>(null, x => x.Clear());

        private void Initialize(RectTransform controller)
        {
            m_ToRebuild = controller;
            m_CachedHashFromTransform = controller.GetHashCode();
        }

        private void Clear()
        {
            m_ToRebuild = null;
            m_CachedHashFromTransform = 0;
        }

        static LayoutRebuilder()
        {
            RectTransform.reapplyDrivenProperties += ReapplyDrivenProperties;
        }

        static void ReapplyDrivenProperties(RectTransform driven)
        {
            MarkLayoutForRebuild(driven);
        }

        public Transform transform { get { return m_ToRebuild; }}

        public bool IsDestroyed()
        {
            return m_ToRebuild == null;
        }

        static void StripDisabledBehavioursFromList(List<Component> components)
        {
            components.RemoveAll(e => e is Behaviour && !((Behaviour)e).isActiveAndEnabled);
        }
        public static void ForceRebuildLayoutImmediate(RectTransform layoutRoot)
        {
            var rebuilder = s_Rebuilders.Get();
            rebuilder.Initialize(layoutRoot);
            rebuilder.Rebuild(CanvasUpdate.Layout);
            s_Rebuilders.Release(rebuilder);
        }

        //CanvasUpdateSystem触发重建
        public void Rebuild(CanvasUpdate executing)
        {
            switch (executing)
            {
                case CanvasUpdate.Layout:
                    // It's unfortunate that we'll perform the same GetComponents querys for the tree 2 times,
                    // but each tree have to be fully iterated before going to the next action,
                    // so reusing the results would entail storing results in a Dictionary or similar,
                    // which is probably a bigger overhead than performing GetComponents multiple times.
                    PerformLayoutCalculation(m_ToRebuild, e => (e as ILayoutElement).CalculateLayoutInputHorizontal());
                    PerformLayoutControl(m_ToRebuild, e => (e as ILayoutController).SetLayoutHorizontal());
                    PerformLayoutCalculation(m_ToRebuild, e => (e as ILayoutElement).CalculateLayoutInputVertical());
                    PerformLayoutControl(m_ToRebuild, e => (e as ILayoutController).SetLayoutVertical());
                    break;
            }
        }

        private void PerformLayoutControl(RectTransform rect, UnityAction<Component> action)
        {
            if (rect == null)
                return;

            var components = ListPool<Component>.Get();
            rect.GetComponents(typeof(ILayoutController), components);
            StripDisabledBehavioursFromList(components);

            // If there are no controllers on this rect we can skip this entire sub-tree
            // We don't need to consider controllers on children deeper in the sub-tree either,
            // since they will be their own roots.
            if (components.Count > 0)
            {
                // Layout control needs to executed top down with parents being done before their children,
                // because the children rely on the sizes of the parents.

                // First call layout controllers that may change their own RectTransform
                for (int i = 0; i < components.Count; i++)
                    if (components[i] is ILayoutSelfController)
                        action(components[i]);

                // Then call the remaining, such as layout groups that change their children, taking their own RectTransform size into account.
                for (int i = 0; i < components.Count; i++)
                    if (!(components[i] is ILayoutSelfController))
                        action(components[i]);

                for (int i = 0; i < rect.childCount; i++)
                    PerformLayoutControl(rect.GetChild(i) as RectTransform, action);
            }

            ListPool<Component>.Release(components);
        }

        private void PerformLayoutCalculation(RectTransform rect, UnityAction<Component> action)
        {
            if (rect == null)
                return;

            var components = ListPool<Component>.Get();
            rect.GetComponents(typeof(ILayoutElement), components);
            StripDisabledBehavioursFromList(components);

            // If there are no controllers on this rect we can skip this entire sub-tree
            // We don't need to consider controllers on children deeper in the sub-tree either,
            // since they will be their own roots.
            if (components.Count > 0  || rect.GetComponent(typeof(ILayoutGroup)))
            {
                // Layout calculations needs to executed bottom up with children being done before their parents,
                // because the parent calculated sizes rely on the sizes of the children.

                for (int i = 0; i < rect.childCount; i++)
                    PerformLayoutCalculation(rect.GetChild(i) as RectTransform, action);

                for (int i = 0; i < components.Count; i++)
                    action(components[i]);
            }

            ListPool<Component>.Release(components);
        }

        //脏标：标记物体进行布局处理
        public static void MarkLayoutForRebuild(RectTransform rect)
        {
            if (rect == null || rect.gameObject == null)
                return;

            var comps = ListPool<Component>.Get();
            bool validLayoutGroup = true;
            RectTransform layoutRoot = rect;
            var parent = layoutRoot.parent as RectTransform;

            //从物体父级路径寻中寻找是否存在布局组件(ILayoutGroup)
            while (validLayoutGroup && !(parent == null || parent.gameObject == null))
            {
                validLayoutGroup = false;
                parent.GetComponents(typeof(ILayoutGroup), comps);

                for (int i = 0; i < comps.Count; ++i)
                {
                    var cur = comps[i];
                    if (cur != null && cur is Behaviour && ((Behaviour)cur).isActiveAndEnabled)
                    {
                        validLayoutGroup = true;
                        layoutRoot = parent;
                        break;
                    }
                }

                parent = parent.parent as RectTransform;
            }

            // We know the layout root is valid if it's not the same as the rect,
            // since we checked that above. But if they're the same we still need to check.
            // 检查自身是否满足布局要求
            if (layoutRoot == rect && !ValidController(layoutRoot, comps))
            {
                ListPool<Component>.Release(comps);
                return;
            }

            MarkLayoutRootForRebuild(layoutRoot);
            ListPool<Component>.Release(comps);
        }

        private static bool ValidController(RectTransform layoutRoot, List<Component> comps)
        {
            if (layoutRoot == null || layoutRoot.gameObject == null)
                return false;

            layoutRoot.GetComponents(typeof(ILayoutController), comps);
            for (int i = 0; i < comps.Count; ++i)
            {
                var cur = comps[i];
                if (cur != null && cur is Behaviour && ((Behaviour)cur).isActiveAndEnabled)
                {
                    return true;
                }
            }

            return false;
        }
        

        private static void MarkLayoutRootForRebuild(RectTransform controller)
        {
            if (controller == null)
                return;

            var rebuilder = s_Rebuilders.Get();
            rebuilder.Initialize(controller);
            //从对象池中生成builder对象，并注册进CanvasUpdate中
            if (!CanvasUpdateRegistry.TryRegisterCanvasElementForLayoutRebuild(rebuilder))
                s_Rebuilders.Release(rebuilder);
        }

        public void LayoutComplete()
        {
            s_Rebuilders.Release(this);
        }

        public void GraphicUpdateComplete()
        {}

        public override int GetHashCode()
        {
            return m_CachedHashFromTransform;
        }

        public override bool Equals(object obj)
        {
            return obj.GetHashCode() == GetHashCode();
        }

        public override string ToString()
        {
            return "(Layout Rebuilder for) " + m_ToRebuild;
        }
    }
}
