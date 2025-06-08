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
        [SerializeField] private float pinchThreshold = 0.8f;
        [SerializeField] private float grabThreshold = 0.8f;
        [SerializeField] private bool showDebugInfo = true;

        [Header("Visual Feedback")]
        [SerializeField] private GameObject leftHandVisual;
        [SerializeField] private GameObject rightHandVisual;
        [SerializeField] private Material handMaterial;
        [SerializeField] private Material pinchMaterial;

        // Hand tracking state
        private bool leftHandTracked = false;
        private bool rightHandTracked = false;
        private bool leftHandPinching = false;
        private bool rightHandPinching = false;
        private bool leftHandGrabbing = false;
        private bool rightHandGrabbing = false;

        // Hand confidence levels
        private OVRHand.TrackingConfidence leftHandConfidence;
        private OVRHand.TrackingConfidence rightHandConfidence;

        // Events for hand tracking
        public System.Action<bool> OnLeftHandTrackingChanged;
        public System.Action<bool> OnRightHandTrackingChanged;
        public System.Action<bool> OnLeftHandPinchChanged;
        public System.Action<bool> OnRightHandPinchChanged;
        public System.Action<bool> OnLeftHandGrabChanged;
        public System.Action<bool> OnRightHandGrabChanged;

        // Finger tip positions for advanced tracking
        private Vector3 leftIndexTip, leftThumbTip;
        private Vector3 rightIndexTip, rightThumbTip;

        void Start()
        {
            Debug.Log("[HandTrackingManager] Starting initialization...");
            InitializeHandTracking();
        }

        void Update()
        {
            UpdateHandTracking();
            UpdateGestureDetection();
            UpdateVisualFeedback();
            
            if (showDebugInfo)
            {
                DisplayDebugInfo();
            }
            
            // Log periodic status every 2 seconds
            if (Time.frameCount % 120 == 0) // Every 2 seconds at 60fps
            {
                LogPeriodicStatus();
            }
        }

        private void InitializeHandTracking()
        {
            Debug.Log("[HandTrackingManager] Searching for hand tracking components...");
            
            // Find OVRHand components if not assigned - search for Building Block naming convention
            if (leftHand == null)
            {
                Debug.Log("[HandTrackingManager] Searching for left hand...");
                // Try multiple possible names for left hand
                leftHand = GameObject.Find("[BuildingBlock] Hand Tracking left")?.GetComponent<OVRHand>();
                if (leftHand != null) Debug.Log("[HandTrackingManager] Found left hand via Building Block name");
                
                if (leftHand == null)
                {
                    leftHand = GameObject.Find("LeftHandAnchor")?.GetComponentInChildren<OVRHand>();
                    if (leftHand != null) Debug.Log("[HandTrackingManager] Found left hand via LeftHandAnchor");
                }
                
                if (leftHand == null)
                {
                    leftHand = FindObjectOfType<OVRHand>(); // Fallback to any OVRHand with left hand type
                    if (leftHand != null) Debug.Log("[HandTrackingManager] Found left hand via FindObjectOfType fallback");
                }
            }
            
            if (rightHand == null)
            {
                Debug.Log("[HandTrackingManager] Searching for right hand...");
                // Try multiple possible names for right hand
                rightHand = GameObject.Find("[BuildingBlock] Hand Tracking right")?.GetComponent<OVRHand>();
                if (rightHand != null) Debug.Log("[HandTrackingManager] Found right hand via Building Block name");
                
                if (rightHand == null)
                {
                    rightHand = GameObject.Find("RightHandAnchor")?.GetComponentInChildren<OVRHand>();
                    if (rightHand != null) Debug.Log("[HandTrackingManager] Found right hand via RightHandAnchor");
                }
                
                if (rightHand == null)
                {
                    // Find all OVRHand components and look for right hand
                    var allHands = FindObjectsOfType<OVRHand>();
                    Debug.Log($"[HandTrackingManager] Found {allHands.Length} total OVRHand components");
                    rightHand = allHands.FirstOrDefault(h => h != leftHand);
                    if (rightHand != null) Debug.Log("[HandTrackingManager] Found right hand via multiple OVRHand search");
                }
            }

            // Find OVRSkeleton components if not assigned
            if (leftHandSkeleton == null)
            {
                Debug.Log("[HandTrackingManager] Searching for left hand skeleton...");
                // Try multiple possible names for left hand skeleton
                leftHandSkeleton = GameObject.Find("[BuildingBlock] Hand Tracking left")?.GetComponent<OVRSkeleton>();
                if (leftHandSkeleton != null) Debug.Log("[HandTrackingManager] Found left skeleton via Building Block name");
                
                if (leftHandSkeleton == null)
                {
                    leftHandSkeleton = GameObject.Find("LeftHandAnchor")?.GetComponentInChildren<OVRSkeleton>();
                    if (leftHandSkeleton != null) Debug.Log("[HandTrackingManager] Found left skeleton via LeftHandAnchor");
                }
                
                if (leftHandSkeleton == null && leftHand != null)
                {
                    leftHandSkeleton = leftHand.GetComponent<OVRSkeleton>();
                    if (leftHandSkeleton != null) Debug.Log("[HandTrackingManager] Found left skeleton on same GameObject as left hand");
                }
            }
            
            if (rightHandSkeleton == null)
            {
                Debug.Log("[HandTrackingManager] Searching for right hand skeleton...");
                // Try multiple possible names for right hand skeleton
                rightHandSkeleton = GameObject.Find("[BuildingBlock] Hand Tracking right")?.GetComponent<OVRSkeleton>();
                if (rightHandSkeleton != null) Debug.Log("[HandTrackingManager] Found right skeleton via Building Block name");
                
                if (rightHandSkeleton == null)
                {
                    rightHandSkeleton = GameObject.Find("RightHandAnchor")?.GetComponentInChildren<OVRSkeleton>();
                    if (rightHandSkeleton != null) Debug.Log("[HandTrackingManager] Found right skeleton via RightHandAnchor");
                }
                
                if (rightHandSkeleton == null && rightHand != null)
                {
                    rightHandSkeleton = rightHand.GetComponent<OVRSkeleton>();
                    if (rightHandSkeleton != null) Debug.Log("[HandTrackingManager] Found right skeleton on same GameObject as right hand");
                }
            }

            // Log initialization status with detailed info
            Debug.Log($"[HandTrackingManager] Initialization Results:");
            Debug.Log($"  Left Hand: {(leftHand != null ? $"Found - {leftHand.gameObject.name}" : "Not Found")}");
            Debug.Log($"  Right Hand: {(rightHand != null ? $"Found - {rightHand.gameObject.name}" : "Not Found")}");
            Debug.Log($"  Left Skeleton: {(leftHandSkeleton != null ? $"Found - {leftHandSkeleton.gameObject.name}" : "Not Found")}");
            Debug.Log($"  Right Skeleton: {(rightHandSkeleton != null ? $"Found - {rightHandSkeleton.gameObject.name}" : "Not Found")}");
            
            // Additional fallback - search all GameObjects for hand tracking components
            if (leftHand == null || rightHand == null)
            {
                Debug.Log("[HandTrackingManager] Searching all GameObjects for hand tracking components...");
                var allGameObjects = FindObjectsOfType<GameObject>();
                foreach (var go in allGameObjects)
                {
                    if (go.name.ToLower().Contains("hand") && go.name.ToLower().Contains("left"))
                    {
                        Debug.Log($"  Found potential left hand object: {go.name}");
                        var hand = go.GetComponent<OVRHand>();
                        if (hand != null && leftHand == null) 
                        {
                            leftHand = hand;
                            Debug.Log($"  ✅ Assigned left hand from: {go.name}");
                        }
                    }
                    else if (go.name.ToLower().Contains("hand") && go.name.ToLower().Contains("right"))
                    {
                        Debug.Log($"  Found potential right hand object: {go.name}");
                        var hand = go.GetComponent<OVRHand>();
                        if (hand != null && rightHand == null) 
                        {
                            rightHand = hand;
                            Debug.Log($"  ✅ Assigned right hand from: {go.name}");
                        }
                    }
                }
            }
            
            // Final status
            if (leftHand == null && rightHand == null)
            {
                Debug.LogError("[HandTrackingManager] ❌ No hand tracking components found! Make sure you have OVRHand components in your scene.");
            }
            else if (leftHand == null)
            {
                Debug.LogWarning("[HandTrackingManager] ⚠️ Left hand not found, but right hand is available.");
            }
            else if (rightHand == null)
            {
                Debug.LogWarning("[HandTrackingManager] ⚠️ Right hand not found, but left hand is available.");
            }
            else
            {
                Debug.Log("[HandTrackingManager] ✅ Both hands found successfully!");
            }
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

        private void UpdateGestureDetection()
        {
            // Left hand gestures
            if (leftHand != null && leftHandTracked)
            {
                // Pinch detection
                bool wasPinching = leftHandPinching;
                float pinchStrength = leftHand.GetFingerPinchStrength(OVRHand.HandFinger.Index);
                leftHandPinching = pinchStrength > pinchThreshold;
                
                if (wasPinching != leftHandPinching)
                {
                    OnLeftHandPinchChanged?.Invoke(leftHandPinching);
                    Debug.Log($"[HandTrackingManager] 👈🤏 Left hand pinch: {(leftHandPinching ? "START" : "END")} (strength: {pinchStrength:F2})");
                }

                // Grab detection (all fingers curled)
                bool wasGrabbing = leftHandGrabbing;
                float grabStrength = (leftHand.GetFingerPinchStrength(OVRHand.HandFinger.Index) +
                                    leftHand.GetFingerPinchStrength(OVRHand.HandFinger.Middle) +
                                    leftHand.GetFingerPinchStrength(OVRHand.HandFinger.Ring) +
                                    leftHand.GetFingerPinchStrength(OVRHand.HandFinger.Pinky)) / 4.0f;
                
                leftHandGrabbing = grabStrength > grabThreshold;
                
                if (wasGrabbing != leftHandGrabbing)
                {
                    OnLeftHandGrabChanged?.Invoke(leftHandGrabbing);
                    Debug.Log($"[HandTrackingManager] 👈✊ Left hand grab: {(leftHandGrabbing ? "START" : "END")} (strength: {grabStrength:F2})");
                }

                // Update finger tip positions
                UpdateFingerTipPositions(leftHand, leftHandSkeleton, true);
            }

            // Right hand gestures
            if (rightHand != null && rightHandTracked)
            {
                // Pinch detection
                bool wasPinching = rightHandPinching;
                float pinchStrength = rightHand.GetFingerPinchStrength(OVRHand.HandFinger.Index);
                rightHandPinching = pinchStrength > pinchThreshold;
                
                if (wasPinching != rightHandPinching)
                {
                    OnRightHandPinchChanged?.Invoke(rightHandPinching);
                    Debug.Log($"[HandTrackingManager] 👉🤏 Right hand pinch: {(rightHandPinching ? "START" : "END")} (strength: {pinchStrength:F2})");
                }

                // Grab detection
                bool wasGrabbing = rightHandGrabbing;
                float grabStrength = (rightHand.GetFingerPinchStrength(OVRHand.HandFinger.Index) +
                                    rightHand.GetFingerPinchStrength(OVRHand.HandFinger.Middle) +
                                    rightHand.GetFingerPinchStrength(OVRHand.HandFinger.Ring) +
                                    rightHand.GetFingerPinchStrength(OVRHand.HandFinger.Pinky)) / 4.0f;
                
                rightHandGrabbing = grabStrength > grabThreshold;
                
                if (wasGrabbing != rightHandGrabbing)
                {
                    OnRightHandGrabChanged?.Invoke(rightHandGrabbing);
                    Debug.Log($"[HandTrackingManager] 👉✊ Right hand grab: {(rightHandGrabbing ? "START" : "END")} (strength: {grabStrength:F2})");
                }

                // Update finger tip positions
                UpdateFingerTipPositions(rightHand, rightHandSkeleton, false);
            }
        }

        private void UpdateFingerTipPositions(OVRHand hand, OVRSkeleton skeleton, bool isLeftHand)
        {
            if (skeleton != null && skeleton.IsInitialized)
            {
                var bones = skeleton.Bones;
                if (bones != null && bones.Count > 0)
                {
                    // Get index finger tip
                    var indexTip = bones.FirstOrDefault(bone => bone.Id == OVRSkeleton.BoneId.Hand_IndexTip);
                    if (indexTip != null)
                    {
                        if (isLeftHand)
                            leftIndexTip = indexTip.Transform.position;
                        else
                            rightIndexTip = indexTip.Transform.position;
                    }

                    // Get thumb tip
                    var thumbTip = bones.FirstOrDefault(bone => bone.Id == OVRSkeleton.BoneId.Hand_ThumbTip);
                    if (thumbTip != null)
                    {
                        if (isLeftHand)
                            leftThumbTip = thumbTip.Transform.position;
                        else
                            rightThumbTip = thumbTip.Transform.position;
                    }
                }
            }
            else if (skeleton == null)
            {
                if (Time.frameCount % 300 == 0) // Log every 5 seconds
                {
                    Debug.LogWarning($"[HandTrackingManager] {(isLeftHand ? "Left" : "Right")} hand skeleton is null");
                }
            }
            else if (!skeleton.IsInitialized)
            {
                if (Time.frameCount % 300 == 0) // Log every 5 seconds
                {
                    Debug.LogWarning($"[HandTrackingManager] {(isLeftHand ? "Left" : "Right")} hand skeleton not initialized yet");
                }
            }
        }

        private void UpdateVisualFeedback()
        {
            // Update left hand visual
            if (leftHandVisual != null)
            {
                leftHandVisual.SetActive(leftHandTracked);
                
                if (leftHandTracked && handMaterial != null && pinchMaterial != null)
                {
                    var renderer = leftHandVisual.GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        renderer.material = leftHandPinching ? pinchMaterial : handMaterial;
                    }
                }
            }

            // Update right hand visual
            if (rightHandVisual != null)
            {
                rightHandVisual.SetActive(rightHandTracked);
                
                if (rightHandTracked && handMaterial != null && pinchMaterial != null)
                {
                    var renderer = rightHandVisual.GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        renderer.material = rightHandPinching ? pinchMaterial : handMaterial;
                    }
                }
            }
        }

        private void DisplayDebugInfo()
        {
            string debugText = "=== HAND TRACKING DEBUG ===\n";
            
            // Left hand info
            debugText += $"LEFT HAND:\n";
            debugText += $"  Tracked: {leftHandTracked}\n";
            debugText += $"  Confidence: {leftHandConfidence}\n";
            debugText += $"  Pinching: {leftHandPinching}\n";
            debugText += $"  Grabbing: {leftHandGrabbing}\n";
            if (leftHandTracked)
            {
                debugText += $"  Index Tip: {leftIndexTip}\n";
                debugText += $"  Thumb Tip: {leftThumbTip}\n";
            }
            
            debugText += $"\nRIGHT HAND:\n";
            debugText += $"  Tracked: {rightHandTracked}\n";
            debugText += $"  Confidence: {rightHandConfidence}\n";
            debugText += $"  Pinching: {rightHandPinching}\n";
            debugText += $"  Grabbing: {rightHandGrabbing}\n";
            if (rightHandTracked)
            {
                debugText += $"  Index Tip: {rightIndexTip}\n";
                debugText += $"  Thumb Tip: {rightThumbTip}\n";
            }

            // Display in console (you can replace this with UI Text if needed)
            if (Time.frameCount % 30 == 0) // Update every 30 frames to avoid spam
            {
                Debug.Log(debugText);
            }
        }

        private void LogPeriodicStatus()
        {
            string status = "[HandTrackingManager] Status: ";
            status += $"L:{(leftHandTracked ? "✅" : "❌")} ";
            status += $"R:{(rightHandTracked ? "✅" : "❌")} ";
            if (leftHandTracked) status += $"LP:{(leftHandPinching ? "🤏" : "👋")} ";
            if (rightHandTracked) status += $"RP:{(rightHandPinching ? "🤏" : "👋")} ";
            Debug.Log(status);
        }

        // Public methods for accessing hand tracking data
        public bool IsLeftHandTracked() => leftHandTracked;
        public bool IsRightHandTracked() => rightHandTracked;
        public bool IsLeftHandPinching() => leftHandPinching;
        public bool IsRightHandPinching() => rightHandPinching;
        public bool IsLeftHandGrabbing() => leftHandGrabbing;
        public bool IsRightHandGrabbing() => rightHandGrabbing;

        public Vector3 GetLeftIndexTip() => leftIndexTip;
        public Vector3 GetRightIndexTip() => rightIndexTip;
        public Vector3 GetLeftThumbTip() => leftThumbTip;
        public Vector3 GetRightThumbTip() => rightThumbTip;

        public float GetLeftPinchStrength() => leftHand?.GetFingerPinchStrength(OVRHand.HandFinger.Index) ?? 0f;
        public float GetRightPinchStrength() => rightHand?.GetFingerPinchStrength(OVRHand.HandFinger.Index) ?? 0f;

        public OVRHand.TrackingConfidence GetLeftHandConfidence() => leftHandConfidence;
        public OVRHand.TrackingConfidence GetRightHandConfidence() => rightHandConfidence;

        // Method to get specific finger position
        public Vector3 GetFingerTipPosition(bool isLeftHand, OVRHand.HandFinger finger)
        {
            OVRSkeleton skeleton = isLeftHand ? leftHandSkeleton : rightHandSkeleton;
            if (skeleton == null || !skeleton.IsInitialized) return Vector3.zero;

            var bones = skeleton.Bones;
            if (bones == null || bones.Count == 0) return Vector3.zero;

            OVRSkeleton.BoneId boneId = finger switch
            {
                OVRHand.HandFinger.Thumb => OVRSkeleton.BoneId.Hand_ThumbTip,
                OVRHand.HandFinger.Index => OVRSkeleton.BoneId.Hand_IndexTip,
                OVRHand.HandFinger.Middle => OVRSkeleton.BoneId.Hand_MiddleTip,
                OVRHand.HandFinger.Ring => OVRSkeleton.BoneId.Hand_RingTip,
                OVRHand.HandFinger.Pinky => OVRSkeleton.BoneId.Hand_PinkyTip,
                _ => OVRSkeleton.BoneId.Hand_IndexTip
            };

            var bone = bones.FirstOrDefault(b => b.Id == boneId);
            return bone?.Transform.position ?? Vector3.zero;
        }

        // Method to check if specific gesture is being performed
        public bool IsPerformingGesture(HandGesture gesture, bool isLeftHand = true)
        {
            OVRHand hand = isLeftHand ? leftHand : rightHand;
            if (hand == null || !hand.IsTracked) return false;

            return gesture switch
            {
                HandGesture.Point => IsPointingGesture(hand),
                HandGesture.Peace => IsPeaceGesture(hand),
                HandGesture.ThumbsUp => IsThumbsUpGesture(hand),
                HandGesture.Fist => IsFistGesture(hand),
                _ => false
            };
        }

        private bool IsPointingGesture(OVRHand hand)
        {
            return hand.GetFingerPinchStrength(OVRHand.HandFinger.Index) < 0.3f &&
                   hand.GetFingerPinchStrength(OVRHand.HandFinger.Middle) > 0.7f &&
                   hand.GetFingerPinchStrength(OVRHand.HandFinger.Ring) > 0.7f &&
                   hand.GetFingerPinchStrength(OVRHand.HandFinger.Pinky) > 0.7f;
        }

        private bool IsPeaceGesture(OVRHand hand)
        {
            return hand.GetFingerPinchStrength(OVRHand.HandFinger.Index) < 0.3f &&
                   hand.GetFingerPinchStrength(OVRHand.HandFinger.Middle) < 0.3f &&
                   hand.GetFingerPinchStrength(OVRHand.HandFinger.Ring) > 0.7f &&
                   hand.GetFingerPinchStrength(OVRHand.HandFinger.Pinky) > 0.7f;
        }

        private bool IsThumbsUpGesture(OVRHand hand)
        {
            return hand.GetFingerPinchStrength(OVRHand.HandFinger.Thumb) < 0.3f &&
                   hand.GetFingerPinchStrength(OVRHand.HandFinger.Index) > 0.7f &&
                   hand.GetFingerPinchStrength(OVRHand.HandFinger.Middle) > 0.7f &&
                   hand.GetFingerPinchStrength(OVRHand.HandFinger.Ring) > 0.7f &&
                   hand.GetFingerPinchStrength(OVRHand.HandFinger.Pinky) > 0.7f;
        }

        private bool IsFistGesture(OVRHand hand)
        {
            return hand.GetFingerPinchStrength(OVRHand.HandFinger.Index) > 0.8f &&
                   hand.GetFingerPinchStrength(OVRHand.HandFinger.Middle) > 0.8f &&
                   hand.GetFingerPinchStrength(OVRHand.HandFinger.Ring) > 0.8f &&
                   hand.GetFingerPinchStrength(OVRHand.HandFinger.Pinky) > 0.8f;
        }
    }

    public enum HandGesture
    {
        Point,
        Peace,
        ThumbsUp,
        Fist
    }
} 