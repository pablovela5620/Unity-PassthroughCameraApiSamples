using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Oculus.Interaction.Input;

namespace HandTracking
{
    public class HandTrackingManager : MonoBehaviour
    {
        [Header("Hand References")]
        [SerializeField] private OVRHand leftHand;
        [SerializeField] private OVRHand rightHand;
        [SerializeField] private OVRSkeleton leftHandSkeleton;
        [SerializeField] private OVRSkeleton rightHandSkeleton;

        [Header("Hand Tracking Settings")]
        [SerializeField] private bool showDebugInfo = true;

        [Header("Visual Feedback")]
        [SerializeField] private GameObject leftHandVisual;
        [SerializeField] private GameObject rightHandVisual;
        [SerializeField] private Material handMaterial;

        // Hand tracking state
        private bool leftHandTracked = false;
        private bool rightHandTracked = false;

        // Hand confidence levels
        private OVRHand.TrackingConfidence leftHandConfidence;
        private OVRHand.TrackingConfidence rightHandConfidence;

        // Events for hand tracking
        public System.Action<bool> OnLeftHandTrackingChanged;
        public System.Action<bool> OnRightHandTrackingChanged;

        void Start()
        {
            Debug.Log("[HandTrackingManager] Starting initialization...");
            InitializeHandTracking();
        }

        void Update()
        {
            UpdateHandTracking();
            UpdateVisualFeedback();
        }

        private void InitializeHandTracking()
        {
            Debug.Log("[HandTrackingManager] Searching for hand tracking components...");
            
            // Find OVRHand components if not assigned - search for Building Block naming convention
            if (leftHand == null)
            {
                Debug.Log("[HandTrackingManager] Searching for left hand...");
                leftHand = GameObject.Find("LeftHandAnchor")?.GetComponentInChildren<OVRHand>();
                if (leftHand != null) Debug.Log("[HandTrackingManager] Found left hand via LeftHandAnchor");
            }
            
            if (rightHand == null)
            {
                Debug.Log("[HandTrackingManager] Searching for right hand...");
                rightHand = GameObject.Find("RightHandAnchor")?.GetComponentInChildren<OVRHand>();
                if (rightHand != null) Debug.Log("[HandTrackingManager] Found right hand via RightHandAnchor");
            }

            // Find OVRSkeleton components if not assigned
            if (leftHandSkeleton == null)
            {
                Debug.Log("[HandTrackingManager] Searching for left hand skeleton...");
                leftHandSkeleton = GameObject.Find("LeftHandAnchor")?.GetComponentInChildren<OVRSkeleton>();
                if (leftHandSkeleton != null) Debug.Log("[HandTrackingManager] Found left skeleton via LeftHandAnchor");
            }
            
            if (rightHandSkeleton == null)
            {
                Debug.Log("[HandTrackingManager] Searching for right hand skeleton...");
                rightHandSkeleton = GameObject.Find("RightHandAnchor")?.GetComponentInChildren<OVRSkeleton>();
                if (rightHandSkeleton != null) Debug.Log("[HandTrackingManager] Found right skeleton via RightHandAnchor");
            }

            // Log initialization status with detailed info
            Debug.Log($"[HandTrackingManager] Initialization Results:");
            Debug.Log($"  Left Hand: {(leftHand != null ? $"Found - {leftHand.gameObject.name}" : "Not Found")}");
            Debug.Log($"  Right Hand: {(rightHand != null ? $"Found - {rightHand.gameObject.name}" : "Not Found")}");
            Debug.Log($"  Left Skeleton: {(leftHandSkeleton != null ? $"Found - {leftHandSkeleton.gameObject.name}" : "Not Found")}");
            Debug.Log($"  Right Skeleton: {(rightHandSkeleton != null ? $"Found - {rightHandSkeleton.gameObject.name}" : "Not Found")}");
            
        }

        private void UpdateHandTracking()
        {
            // Update left hand tracking
            if (leftHand != null)
            {
                bool wasTracked = leftHandTracked;
                leftHandTracked = leftHand.IsTracked;
                leftHandConfidence = leftHand.GetFingerConfidence(OVRHand.HandFinger.Index);

                if (wasTracked != leftHandTracked)
                {
                    OnLeftHandTrackingChanged?.Invoke(leftHandTracked);
                    Debug.Log($"[HandTrackingManager] 👈 Left hand tracking changed: {(leftHandTracked ? "TRACKED" : "LOST")}");
                }
            }

            // Update right hand tracking
            if (rightHand != null)
            {
                bool wasTracked = rightHandTracked;
                rightHandTracked = rightHand.IsTracked;
                rightHandConfidence = rightHand.GetFingerConfidence(OVRHand.HandFinger.Index);

                if (wasTracked != rightHandTracked)
                {
                    OnRightHandTrackingChanged?.Invoke(rightHandTracked);
                    Debug.Log($"[HandTrackingManager] 👉 Right hand tracking changed: {(rightHandTracked ? "TRACKED" : "LOST")}");
                }
            }
        }

        private void UpdateVisualFeedback()
        {
            // Update left hand visual
            if (leftHandVisual != null)
            {
                leftHandVisual.SetActive(leftHandTracked);
            }

            // Update right hand visual
            if (rightHandVisual != null)
            {
                rightHandVisual.SetActive(rightHandTracked);
            }
        }

        // Public methods for accessing hand tracking data
        public bool IsLeftHandTracked() => leftHandTracked;
        public bool IsRightHandTracked() => rightHandTracked;

        public OVRHand.TrackingConfidence GetLeftHandConfidence() => leftHandConfidence;
        public OVRHand.TrackingConfidence GetRightHandConfidence() => rightHandConfidence;

        // Method to get all hand tracking points for a specific hand
        public List<Vector3> GetAllHandPoints(bool isLeftHand)
        {
            List<Vector3> handPoints = new List<Vector3>();
            OVRSkeleton skeleton = isLeftHand ? leftHandSkeleton : rightHandSkeleton;
            bool isTracked = isLeftHand ? leftHandTracked : rightHandTracked;

            if (skeleton == null || !skeleton.IsInitialized || !isTracked)
            {
                // Return empty list if hand is not available or tracked
                return handPoints;
            }

            var bones = skeleton.Bones;
            if (bones == null || bones.Count == 0)
            {
                return handPoints;
            }

            // Extract all bone positions
            foreach (var bone in bones)
            {
                if (bone != null && bone.Transform != null)
                {
                    handPoints.Add(bone.Transform.position);
                }
            }

            return handPoints;
        }

        // Method to get all hand tracking points for the left hand
        public List<Vector3> GetLeftHandPoints()
        {
            return GetAllHandPoints(true);
        }

        // Method to get all hand tracking points for the right hand
        public List<Vector3> GetRightHandPoints()
        {
            return GetAllHandPoints(false);
        }

        // Method to get all hand tracking points for both hands
        public (List<Vector3> leftHand, List<Vector3> rightHand) GetBothHandsPoints()
        {
            return (GetAllHandPoints(true), GetAllHandPoints(false));
        }
 
    }
} 