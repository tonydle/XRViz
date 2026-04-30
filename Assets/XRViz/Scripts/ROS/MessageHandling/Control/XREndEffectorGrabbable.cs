using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

namespace Unity.Robotics
{
    public class XREndEffectorGrabbable : MonoBehaviour
    {
        [SerializeField, Tooltip("Used to create the grabbable Marker mesh")]
        private string _lastLinkName;

        [SerializeField, Tooltip("Should be child of the last link")]
        private string _endEffectorName;

        [SerializeField, Tooltip("Affordance prefab to be instantiated")]
        private GameObject affordancePrefab;

        private GameObject endEffector;
        private GameObject lastLink;
        private GameObject lastLinkMarker;
        private Dictionary<GameObject, GameObject> markerToOriginalMap = new Dictionary<GameObject, GameObject>();
        private GameObject markerEndEffector;
        private bool isActivated = false;
        private bool isSelected = false;
        private UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable grabInteractable;
        
        void Start()
        {
            lastLink = FindChildRecursive(transform, _lastLinkName);
            if (lastLink != null)
            {
                endEffector = FindChildRecursive(lastLink.transform, _endEffectorName);
                if (endEffector != null)
                {
                    CreateMarkers(lastLink);
                }
            }
        }

        private void Update()
        {
            // Update the collider position and rotation to match the marker
            if (lastLinkMarker != null && !lastLinkMarker.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>().isSelected)
            {
                MeshCollider markerCollider = lastLinkMarker.GetComponent<MeshCollider>();
                if (markerCollider != null)
                {
                    markerCollider.transform.position = lastLinkMarker.transform.position;
                    markerCollider.transform.rotation = lastLinkMarker.transform.rotation;
                }
            }

            // Update the local transforms for all markers except lastLinkMarker
            foreach (var pair in markerToOriginalMap)
            {
                if (pair.Key == lastLinkMarker) continue; // skip the lastLinkMarker

                pair.Key.transform.localRotation = pair.Value.transform.localRotation;
                pair.Key.transform.localPosition = pair.Value.transform.localPosition;
            }
        }

        public bool IsActivated()
        {
            return isActivated;
        }

        public bool IsSelected()
        {
            return isSelected;
        }

        public void GetEndEffectorPositionAndRotation(out Vector3 position, out Quaternion rotation)
        {
            position = endEffector.transform.position;
            rotation = endEffector.transform.rotation;
        }

        public void GetTargetPositionAndRotation(out Vector3 position, out Quaternion rotation)
        {
            position = markerEndEffector.transform.position;
            rotation = markerEndEffector.transform.rotation;
        }

        public void SetEnabled(bool enabled)
        {
            if (lastLinkMarker != null)
            {
                lastLinkMarker.SetActive(enabled);
                grabInteractable.enabled = enabled;
                isSelected = false;
                isActivated = false;
            }
        }
 
        private GameObject FindChildRecursive(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name)
                {
                    return child.gameObject;
                }

                GameObject result = FindChildRecursive(child, name);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        private void CreateMarkers(GameObject currentLink, GameObject parentMarker = null)
        {
            /* Find the visuals elements */
            Transform visualsTransform = currentLink.transform.Find("Visuals");
            if (visualsTransform == null) return;

            Transform visualsUnnamedTransform = visualsTransform.Find("unnamed");
            if (visualsUnnamedTransform == null) return;

            MeshFilter targetMeshFilter = visualsUnnamedTransform.GetComponentInChildren<MeshFilter>();
            if (targetMeshFilter == null) return;
            /* End of finding the visuals elements */

            /* Find the collisions elements */
            Transform collisionsTransform = currentLink.transform.Find("Collisions");
            if (collisionsTransform == null) return;

            Transform collisionsUnnamedTransform = collisionsTransform.Find("unnamed");
            if (collisionsUnnamedTransform == null) return;

            MeshCollider[] targetMeshColliders = collisionsUnnamedTransform.GetComponentsInChildren<MeshCollider>();
            if (targetMeshColliders.Length <= 0) return;
            /* End of finding the collisions elements */

            GameObject marker = new("Marker_" + currentLink.name);
            if (currentLink == lastLink)
            {
                marker.transform.SetParent(currentLink.transform, false);
                markerEndEffector = new GameObject("MarkerEndEffector");
                markerEndEffector.transform.SetPositionAndRotation(endEffector.transform.position, endEffector.transform.rotation);
                markerEndEffector.transform.SetParent(marker.transform, true);
            }
            else
            {
                marker.transform.SetParent(parentMarker.transform, false);
                markerToOriginalMap.Add(marker, currentLink);
            }

            foreach (Transform child in currentLink.transform)
            {
                CreateMarkers(child.gameObject, marker);
            }

            /* Copy the visuals elements' hierarchy, position, rotation and scale */
            GameObject markerVisuals = new("Visuals");
            markerVisuals.transform.SetParent(marker.transform, false);
            markerVisuals.transform.SetLocalPositionAndRotation(visualsTransform.localPosition, visualsTransform.localRotation);
            markerVisuals.transform.localScale = visualsTransform.localScale;

            GameObject markerVisualsUnnamed = new("unnamed");
            markerVisualsUnnamed.transform.SetParent(markerVisuals.transform, false);
            markerVisualsUnnamed.transform.SetLocalPositionAndRotation(visualsUnnamedTransform.localPosition, visualsUnnamedTransform.localRotation);
            markerVisualsUnnamed.transform.localScale = visualsUnnamedTransform.localScale;
            /* End of copying the visuals elements' hierarchy, position, rotation and scale */

            // Create a child GameObject for the marker to store the visuals components
            GameObject visualsMesh = new("mesh");
            visualsMesh.transform.SetParent(markerVisualsUnnamed.transform, false);
            visualsMesh.transform.SetLocalPositionAndRotation(targetMeshFilter.transform.localPosition, targetMeshFilter.transform.localRotation);
            visualsMesh.transform.localScale = targetMeshFilter.transform.localScale;
            MeshFilter visualsMeshFilter = visualsMesh.AddComponent<MeshFilter>();
            visualsMeshFilter.sharedMesh = targetMeshFilter.sharedMesh;
            MeshRenderer visualsMeshRenderer = visualsMesh.AddComponent<MeshRenderer>();
            visualsMeshRenderer.materials = new Material[0];

            /* Copy the collisions elements' hierarchy, position, rotation and scale */
            GameObject markerCollisions = new("Collisions");
            markerCollisions.transform.SetParent(marker.transform, false);
            markerCollisions.transform.SetLocalPositionAndRotation(collisionsTransform.localPosition, collisionsTransform.localRotation);
            markerCollisions.transform.localScale = collisionsTransform.localScale;

            GameObject markerCollisionsUnnamed = new("unnamed");
            markerCollisionsUnnamed.transform.SetParent(markerCollisions.transform, false);
            markerCollisionsUnnamed.transform.SetLocalPositionAndRotation(collisionsUnnamedTransform.localPosition, collisionsUnnamedTransform.localRotation);
            markerCollisionsUnnamed.transform.localScale = collisionsUnnamedTransform.localScale;
            /* End of copying the collisions elements' hierarchy, position, rotation and scale */

            // Create a child GameObject for the marker to store the collisions and interaction components
            GameObject collisionsMesh = new("mesh")
            {
                layer = LayerMask.NameToLayer("XR Interactables")
            };
            collisionsMesh.transform.SetParent(markerCollisionsUnnamed.transform, false);
            collisionsMesh.transform.SetLocalPositionAndRotation(targetMeshColliders[0].transform.localPosition, targetMeshColliders[0].transform.localRotation);
            collisionsMesh.transform.localScale = targetMeshColliders[0].transform.localScale;

            // Copy the meshes from the targetMeshColliders to the collisionsMesh
            for (int i = 0; i < targetMeshColliders.Length; i++)
            {
                MeshCollider markerCollider = collisionsMesh.AddComponent<MeshCollider>();
                markerCollider.sharedMesh = targetMeshColliders[i].sharedMesh;
                markerCollider.convex = true;
            }

            if (currentLink == lastLink)
            {
                // Add the XRGrabInteractable component to the lastLinkMarker
                AddXRGrabbableFunctionality(marker);
                lastLinkMarker = marker;
            }
        }

        private void AddXRGrabbableFunctionality(GameObject marker)
        {
            grabInteractable = marker.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
            grabInteractable.movementType = UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable.MovementType.Instantaneous;
            grabInteractable.useDynamicAttach = true;
            grabInteractable.throwOnDetach = false;

            // Add this inside the CreateMarker function after creating the XRGrabInteractable component
            Rigidbody markerRigidbody = marker.GetComponent<Rigidbody>();
            if (markerRigidbody == null)
            {
                markerRigidbody = marker.AddComponent<Rigidbody>();
            }
            markerRigidbody.isKinematic = true;

            grabInteractable.selectEntered.AddListener((SelectEnterEventArgs args) => {
                markerRigidbody.isKinematic = false;
                isSelected = true;
            });

            grabInteractable.selectExited.AddListener((SelectExitEventArgs args) => {
                markerRigidbody.isKinematic = true;
                isSelected = false;
                isActivated = false;
                marker.transform.localPosition = Vector3.zero;
                marker.transform.localRotation = Quaternion.identity;
            });

            grabInteractable.activated.AddListener((ActivateEventArgs args) => {
                isActivated = true;
            });

            grabInteractable.deactivated.AddListener((DeactivateEventArgs args) => {
                isActivated = false;
            });
        }
    }
}