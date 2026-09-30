using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.Robotics
{
    // Makes and unmakes copies of a visualisation at runtime, so one scene can show several laser
    // scans, several point clouds or several camera windows at once - each bound to a different
    // topic.
    //
    // Why copies of a live object rather than prefabs: the scene generator already builds exactly
    // one correctly wired instance of each kind, with its subscribers, its visualiser, its TF
    // anchor and its grab handle all pointed at each other. A prefab would be a second definition
    // of the same thing, and the two would drift the first time either was edited. The generated
    // instance IS the template - Instantiate copies the wiring, and the two things Instantiate
    // cannot get right are fixed up here:
    //
    //   * The grab handle is a separate ROOT object (it has to be - a handle parented under what
    //     it moves would move itself), so its reference to the visualisation points outside the
    //     copied hierarchy and is NOT remapped. Left alone, the new sphere drags the old cloud.
    //   * Names and labels are duplicated, and "which of these three identical rows is the one I
    //     just added" is the whole question the Topics, TF and Scene lists have to answer.
    //
    // Copies start with NO topic, exactly like the generated scene does. Cloning the template's
    // topic would mean every new window came up showing the same camera - and, worse, two
    // subscribers on one topic, which is the case ROSConnection.Unsubscribe handles badly (see
    // RosSubscriberRegistry). Sharing a topic deliberately is fine and supported; arriving at it
    // by accident, on every single press, is not.
    //
    // The template itself is never destroyed - the count floor is one. A kind you do not want is
    // hidden from the Scene tab or detached from its topic with the Topics tab's ✕; destroying
    // the last one would leave nothing to copy from and no way back.
    public class VisualizationSpawner : MonoBehaviour
    {
        [Serializable]
        public class Kind
        {
            [Tooltip("What the Views page calls this row.")]
            public string Label;

            [Tooltip("The scene's own instance of this visualisation, which every copy is made " +
                     "from. Never destroyed.")]
            public GameObject Template;

            [Tooltip("The template's grab handle - a separate root object, so it has to be named " +
                     "here rather than found inside the template.")]
            public PlacementHandle Handle;

            [Tooltip("How far to the side of the one it was copied from a new copy appears, in " +
                     "metres. Along that object's own right, so a row of camera windows lines up " +
                     "in the plane they face you in.")]
            public float SpacingMetres = 0.45f;

            [Tooltip("Ceiling on copies. Every instance is a live subscriber, and the socket is " +
                     "the limit long before the GPU is.")]
            public int MaxCount = 6;

            // Newest last, so removing is "the one you just added" - the only ordering anyone
            // can predict from the other side of a button press
            [NonSerialized] public readonly List<GameObject> Copies = new List<GameObject>();
            [NonSerialized] public readonly List<PlacementHandle> CopyHandles = new List<PlacementHandle>();
        }

        [SerializeField] private Kind[] _kinds = new Kind[0];

        public int KindCount => _kinds != null ? _kinds.Length : 0;

        public string LabelOf(int index)
        {
            var kind = KindAt(index);
            if (kind == null)
                return "?";
            if (!string.IsNullOrEmpty(kind.Label))
                return kind.Label;
            return kind.Template != null ? kind.Template.name : "?";
        }

        // Live instances of this kind, template included. Counted rather than cached: a copy can
        // be destroyed by something other than this component (a scene reload, a script, the
        // Editor), and a stale count would offer a Remove that does nothing.
        public int CountOf(int index)
        {
            var kind = KindAt(index);
            if (kind == null)
                return 0;

            Compact(kind);
            return (kind.Template != null ? 1 : 0) + kind.Copies.Count;
        }

        public bool CanAdd(int index)
        {
            var kind = KindAt(index);
            return kind != null && kind.Template != null && CountOf(index) < Mathf.Max(1, kind.MaxCount);
        }

        public bool CanRemove(int index)
        {
            var kind = KindAt(index);
            Compact(kind);
            return kind != null && kind.Copies.Count > 0;
        }

        // One more of this kind, beside the newest one there is. Returns false with a reason to
        // show in the panel's footer - a press that cannot work has to say why, because from the
        // user's side "the count did not change" is all they see.
        public bool Add(int index, out string message)
        {
            var kind = KindAt(index);
            if (kind == null)
            {
                message = "no such visualisation";
                return false;
            }

            if (kind.Template == null)
            {
                message = $"<color=#FF5252>{LabelOf(index)} is missing from the scene</color>";
                return false;
            }

            Compact(kind);
            int count = (kind.Template != null ? 1 : 0) + kind.Copies.Count;
            if (count >= Mathf.Max(1, kind.MaxCount))
            {
                message = $"<color=#FFB300>{LabelOf(index)}: {kind.MaxCount} is the limit</color>";
                return false;
            }

            GameObject source = kind.Copies.Count > 0 ? kind.Copies[kind.Copies.Count - 1] : kind.Template;
            int number = count + 1;
            string copyName = $"{kind.Template.name} {number}";

            var copy = Instantiate(source);
            copy.name = copyName;

            // A copy of something hidden would arrive already hidden, which reads exactly like a
            // press that did nothing
            copy.SetActive(true);

            // Beside the object it was copied from rather than at the template's original pose:
            // the new one should turn up next to the one you are looking at, not back where the
            // generator first put it.
            Transform from = source.transform;
            copy.transform.SetPositionAndRotation(
                from.position + from.right * kind.SpacingMetres, from.rotation);

            ClearTopics(copy);
            Relabel(copy, kind.Template.name, copyName);

            PlacementHandle handle = CloneHandle(kind, copy.transform, number);
            if (handle != null)
            {
                // The copy's TF anchor must drive its OWN handle. Set before the copy's Start
                // runs, so an anchor that comes up already in TF mode suspends the right sphere.
                foreach (var anchor in copy.GetComponentsInChildren<TfAnchor>(true))
                    anchor.SetHandle(handle);
                kind.CopyHandles.Add(handle);
            }
            else
            {
                kind.CopyHandles.Add(null);
            }

            kind.Copies.Add(copy);

            message = $"added {copyName} — pick its topic on the Topics tab";
            return true;
        }

        // Remove the newest copy, handle and all. Destroying the subscribers is what detaches
        // them from the endpoint: RosSubscriber unsubscribes in OnDestroy, and puts back any
        // other subscriber that was sharing the topic it just dropped.
        public bool Remove(int index, out string message)
        {
            var kind = KindAt(index);
            Compact(kind);

            if (kind == null || kind.Copies.Count == 0)
            {
                message = $"<color=#FFB300>{LabelOf(index)}: the scene's own one cannot be " +
                          "removed — hide it on the Scene tab, or detach its topic with ✕</color>";
                return false;
            }

            int last = kind.Copies.Count - 1;
            GameObject copy = kind.Copies[last];
            PlacementHandle handle = last < kind.CopyHandles.Count ? kind.CopyHandles[last] : null;
            string removedName = copy != null ? copy.name : LabelOf(index);

            kind.Copies.RemoveAt(last);
            if (last < kind.CopyHandles.Count)
                kind.CopyHandles.RemoveAt(last);

            if (handle != null)
                Destroy(handle.gameObject);
            if (copy != null)
                Destroy(copy);

            message = $"removed {removedName}";
            return true;
        }

        private Kind KindAt(int index)
        {
            return _kinds != null && index >= 0 && index < _kinds.Length ? _kinds[index] : null;
        }

        // Drop copies that something else destroyed, so the count is of what is actually there
        private static void Compact(Kind kind)
        {
            if (kind == null)
                return;

            for (int i = kind.Copies.Count - 1; i >= 0; i--)
            {
                if (kind.Copies[i] != null)
                    continue;
                kind.Copies.RemoveAt(i);
                if (i < kind.CopyHandles.Count)
                {
                    if (kind.CopyHandles[i] != null)
                        Destroy(kind.CopyHandles[i].gameObject);
                    kind.CopyHandles.RemoveAt(i);
                }
            }
        }

        // Every subscriber in the copy starts unbound. Called before the copy's Start has run, so
        // SetTopic takes its pre-Start branch and simply stores the empty name - nothing ever
        // subscribes, rather than subscribing to the template's topic and immediately leaving it.
        private static void ClearTopics(GameObject copy)
        {
            foreach (var behaviour in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is IRosTopicBinding binding)
                    binding.SetTopic(string.Empty);
            }
        }

        // Rename the copy's parts the way the generator names the originals: the subscriber
        // children carry their visualisation's name as a prefix ("RGBd Camera Depth"), and the
        // Topics browser labels every binding by its GameObject name. Without this a scene with
        // three RGBD cameras offers three identical "RGBd Camera Depth" targets and no way to
        // tell which cloud each one feeds.
        private static void Relabel(GameObject copy, string templateName, string copyName)
        {
            foreach (var child in copy.GetComponentsInChildren<Transform>(true))
            {
                if (child.gameObject == copy)
                    continue;
                if (child.name.StartsWith(templateName, StringComparison.Ordinal))
                    child.name = copyName + child.name.Substring(templateName.Length);
            }

            foreach (var visibility in copy.GetComponentsInChildren<VisibilityTarget>(true))
                visibility.SetLabel(copyName);

            foreach (var anchor in copy.GetComponentsInChildren<TfAnchor>(true))
                anchor.SetLabel(copyName);
        }

        private static PlacementHandle CloneHandle(Kind kind, Transform target, int number)
        {
            if (kind.Handle == null)
                return null;

            var handleGo = Instantiate(kind.Handle.gameObject);
            handleGo.name = $"{kind.Handle.gameObject.name} {number}";

            var handle = handleGo.GetComponent<PlacementHandle>();
            if (handle == null)
            {
                Destroy(handleGo);
                return null;
            }

            // Points at the template until told otherwise - Instantiate only remaps references
            // that stay inside the copied hierarchy, and this one deliberately leaves it
            handle.SetTarget(target);
            return handle;
        }
    }
}
