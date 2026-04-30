using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using System.Collections.Generic;
using TMPro;

namespace MRRobotRegistration
{
    public class RobotRegistrationTool : MonoBehaviour
    {
        public ArticulationBody virtualRobotBaseLink;
        public GameObject pointsSpawnerPrefab;
        public Transform pointsSpawnerSpawnPoint;
        public TMP_Text realPointCountText;
        public TMP_Text virtualPointCountText;

        private GameObject pointsSpawnerObject;
        private List<Vector3> realPoints = new();
        private List<Vector3> virtualPoints = new();
        private int registerationStep = 0;

        // List of mesh renderers to hide when the registration is complete
        private List<MeshRenderer> virtualRobotMeshRenderers = new();

        // Restart the registration process
        public void RestartRegistration()
        {
            // Destroy the old points spawner
            Destroy(pointsSpawnerObject);
            // Create a new points spawner
            pointsSpawnerObject = Instantiate(pointsSpawnerPrefab, pointsSpawnerSpawnPoint.position, Quaternion.identity);
            // Set the mode to spawn real points
            pointsSpawnerObject.GetComponent<PointsSpawner>().SetMode(1);
            // Reset the registration step
            registerationStep = 1;
        }

        private void Awake() {
            // Find all the mesh renderers in the virtual robot
            virtualRobotMeshRenderers.AddRange(virtualRobotBaseLink.GetComponentsInChildren<MeshRenderer>());
        }

        // Set the visibility of the virtual robot
        public void SetVirtualRobotVisibility(bool visible)
        {
            foreach (MeshRenderer meshRenderer in virtualRobotMeshRenderers)
            {
                meshRenderer.enabled = visible;
            }
        }

        private void Update() {
            if(registerationStep == 1)
            {
                // Update the real and virtual point counts
                realPoints = pointsSpawnerObject.GetComponent<PointsSpawner>().GetRealPoints();
                virtualPoints = pointsSpawnerObject.GetComponent<PointsSpawner>().GetVirtualPoints();

                // Update the point count text
                realPointCountText.text = realPoints.Count.ToString();
                virtualPointCountText.text = virtualPoints.Count.ToString();

                // If there are at least 3 points, move to the next step
                if(realPoints.Count >= 3)
                {
                    registerationStep = 2;
                    // Set the mode to spawn virtual points
                    pointsSpawnerObject.GetComponent<PointsSpawner>().SetMode(2);
                }
            }
            else if(registerationStep == 2)
            {
                // Update the real and virtual point counts
                realPoints = pointsSpawnerObject.GetComponent<PointsSpawner>().GetRealPoints();
                virtualPoints = pointsSpawnerObject.GetComponent<PointsSpawner>().GetVirtualPoints();

                // Update the point count text
                realPointCountText.text = realPoints.Count.ToString();
                virtualPointCountText.text = virtualPoints.Count.ToString();

                // If there are at least 3 points, move to the next step
                if(virtualPoints.Count >= 3)
                {
                    registerationStep = 3;
                    // Set the mode to not spawn points
                    pointsSpawnerObject.GetComponent<PointsSpawner>().SetMode(0);
                }
            }
        }

        // Function to calculate the centroid of a set of points
        Vector3 CalculateCentroid(List<Vector3> points)
        {
            Vector3 centroid = Vector3.zero;
            foreach (Vector3 point in points)
            {
                centroid += point;
            }
            centroid /= points.Count;
            return centroid;
        }

        Quaternion CalculateRotation(List<Vector3> virtualPoints, List<Vector3> realPoints)
        {
            // Assuming the first two points form the primary direction
            Vector3 virtualDirection = virtualPoints[1] - virtualPoints[0];
            Vector3 realDirection = realPoints[1] - realPoints[0];

            // Calculate the rotation needed to align these two directions
            Quaternion rotation = Quaternion.FromToRotation(virtualDirection, realDirection);
            return rotation;
        }

        // Function to align the virtual robot with the real one
        private void AlignRobot()
        {
            // Create a new GameObject to manipulate in place of the virtual robot
            GameObject virtualRobotStandIn = new("VirtualRobotStandIn");
            virtualRobotStandIn.transform.SetPositionAndRotation(virtualRobotBaseLink.transform.position, virtualRobotBaseLink.transform.rotation);

            Vector3 virtualCentroid = CalculateCentroid(virtualPoints);
            Vector3 realCentroid = CalculateCentroid(realPoints);

            // Calculate the translation needed to align the pivot with the real centroid
            Vector3 translation = realCentroid - virtualCentroid;
            // Translate the object to align the pivot with the real centroid
            virtualRobotStandIn.transform.position += translation;

            // Calculate the rotation
            Quaternion rotation = CalculateRotation(virtualPoints, realPoints);
            rotation.ToAngleAxis(out float angle, out Vector3 axis);
            // Apply the rotation around the real centroid
            virtualRobotStandIn.transform.RotateAround(realCentroid, axis, angle);

            // Teleport the virtual robot to the new position and rotation
            virtualRobotBaseLink.TeleportRoot(virtualRobotStandIn.transform.position, virtualRobotStandIn.transform.rotation);
        }

        public void Confirm()
        {
            if(registerationStep == 3)
            {
                // Perform the registration
                AlignRobot();
                // Destroy the points spawner
                Destroy(pointsSpawnerObject);
                // Reset the registration step
                registerationStep = 0;
            }
        }
    }
}