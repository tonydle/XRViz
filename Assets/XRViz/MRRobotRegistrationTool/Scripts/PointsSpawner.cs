using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using System.Collections.Generic;

namespace MRRobotRegistration
{
    [RequireComponent(typeof(UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable))]
    public class PointsSpawner : MonoBehaviour
    {
        public GameObject realPointPrefab;
        public GameObject virtualPointPrefab;
        public Transform spawnPoint;
        private UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable grabInteractable;

        private readonly List<Vector3> realPoints = new();
        private readonly List<Vector3> virtualPoints = new();

        // Store the points' game objects
        private readonly List<GameObject> realPointObjects = new();
        private readonly List<GameObject> virtualPointObjects = new();

        // current mode (0 == not spawning, 1 == real, 2 == virtual)
        private int mode = 0;

        private void Awake()
        {
            grabInteractable = GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
            grabInteractable.activated.AddListener(OnActivated);
        }

        private void OnActivated(ActivateEventArgs arg)
        {
            if(mode == 1)
            {
                // Spawn a real world point
                GameObject realPoint = Instantiate(realPointPrefab, spawnPoint.position, Quaternion.identity);
                realPoints.Add(realPoint.transform.position);
                realPointObjects.Add(realPoint);
            }
            else if(mode == 2)
            {
                // Spawn a virtual point
                GameObject virtualPoint = Instantiate(virtualPointPrefab, spawnPoint.position, Quaternion.identity);
                virtualPoints.Add(virtualPoint.transform.position);
                virtualPointObjects.Add(virtualPoint);
            }
        }

        // Function to set the mode to spawn real or virtual points
        public void SetMode(int newMode)
        {
            mode = newMode;
        }

        // Function to return the real points
        public List<Vector3> GetRealPoints()
        {
            return realPoints;
        }

        // Function to return the virtual points
        public List<Vector3> GetVirtualPoints()
        {
            return virtualPoints;
        }

        // Function to clear the real and virtual points
        public void ClearPoints()
        {
            foreach (GameObject point in realPointObjects)
            {
                Destroy(point);
            }
            foreach (GameObject point in virtualPointObjects)
            {
                Destroy(point);
            }
            realPoints.Clear();
            virtualPoints.Clear();
        }

        // Clear points on destroy
        private void OnDestroy()
        {
            ClearPoints();
        }
    }
}