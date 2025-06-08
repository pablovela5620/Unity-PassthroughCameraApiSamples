// Copyright (c) Meta Platforms, Inc. and affiliates.
//
// Quest-3 passthrough (or Main Camera in-editor) → ARFlow RGB uploader.

using System.Collections;
using System.Threading.Tasks;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using TMPro;
using Google.Protobuf;
using ARFlow;
using Unity.Profiling;
using HandTracking;
using System.Collections.Generic;

namespace PassthroughCameraSamples.ARFlowBridge
{
    [MetaCodeSample("PassthroughCameraApiSamples-CameraViewer+ARFlow")]
    public class ARFlowMetaQuest : MonoBehaviour
    {
        [Header("Passthrough webcam (Quest)")]
        [SerializeField] private WebCamTextureManager m_webCamTextureManager;

        [Header("Preview / Debug (optional)")]
        [SerializeField] private RawImage m_previewImage;
        [SerializeField] private Text     m_debugText;

        [Header("ARFlow connection")]
        [Tooltip("gRPC endpoint, e.g. 192.168.1.189:8500")]
        [SerializeField] private string   m_serverAddress = "192.168.1.189:8500";
        [Tooltip("Device name shown on the ARFlow dashboard")]
        [SerializeField] private string   m_deviceName    = "Quest3_Passthrough";
        [Tooltip("Text element to display FPS and latency")]
        [SerializeField] private TextMeshProUGUI m_statsText;
        [Tooltip("Factor to scale down the frame resolution before sending (0.1 to 1.0)")]
        [SerializeField, Range(0.1f, 1.0f)] private float m_renderScale = 0.5f;

        [Header("Hand Tracking Integration")]
        [Tooltip("Hand tracking manager for pose logging")]
        [SerializeField] private HandTrackingManager m_handTrackingManager;
        [Tooltip("Enable hand pose logging")]
        [SerializeField] private bool m_enableHandPoseLogging = true;
        [Tooltip("Log hand poses every N frames (to avoid spam)")]
        [SerializeField] private int m_handPoseLogInterval = 30;

        private ARFlowClient _client;
        private Texture2D    _cpuTex;
        private Vector2Int   _res;

        private bool   _usingMainCamera;
        private Camera _mainCam;
        private RenderTexture _mainRT;

        private bool   _streaming;
        private float  _latencyMs;
        private int    _framesSentInInterval;
        private float  _intervalTimer;
        private float  _uploadFps;
        private bool   _isProcessingFrame;

        // Hand tracking variables
        private int    _frameCountForHandLogging = 0;
        private bool   _lastLeftHandTracked = false;
        private bool   _lastRightHandTracked = false;

        private static readonly ProfilerMarker CopyMarker = new ProfilerMarker("CopyRGBtoCPU");
        private TaskCompletionSource<bool> _gpuReadbackTCS;

        private void Start() => StartCoroutine(Init());

        private IEnumerator Init()
        {
            // Initialize hand tracking manager if not assigned
            if (m_handTrackingManager == null)
            {
                m_handTrackingManager = FindObjectOfType<HandTrackingManager>();
                if (m_handTrackingManager == null && m_enableHandPoseLogging)
                {
                    Debug.LogWarning("[ARFlowMetaQuest] HandTrackingManager not found. Hand pose logging will be disabled.");
                    m_enableHandPoseLogging = false;
                }
            }

            if (m_enableHandPoseLogging && m_handTrackingManager != null)
            {
                // Subscribe to hand tracking events
                m_handTrackingManager.OnLeftHandTrackingChanged += OnLeftHandTrackingChanged;
                m_handTrackingManager.OnRightHandTrackingChanged += OnRightHandTrackingChanged;
                Debug.Log("[ARFlowMetaQuest] Hand tracking integration enabled.");
            }

#if UNITY_EDITOR
            if (m_webCamTextureManager == null || m_webCamTextureManager.WebCamTexture == null)
            {
                _usingMainCamera = true;
                _mainCam = Camera.main;
                if (_mainCam == null)
                {
                    Debug.LogError("[ARFlowMetaQuest] No Main Camera found for editor debug.");
                    yield break;
                }

                _res = new Vector2Int(_mainCam.pixelWidth, _mainCam.pixelHeight);
                _mainRT = new RenderTexture(_res.x, _res.y, 24, RenderTextureFormat.ARGB32);
                _mainCam.targetTexture = _mainRT;
                if (m_previewImage) m_previewImage.texture = _mainRT;

                Log($"Editor debug: streaming Main Camera {_res.x}×{_res.y}");
                ConnectAndStart();
                yield break;
            }
#endif
            while (m_webCamTextureManager.WebCamTexture == null ||
                   m_webCamTextureManager.WebCamTexture.width <= 16)
            {
                yield return null;
            }

            var camTex = m_webCamTextureManager.WebCamTexture;
            _res = new Vector2Int(camTex.width, camTex.height);

            if (m_previewImage) m_previewImage.texture = camTex;
            Log($"WebCam ready {_res.x}×{_res.y}");

            ConnectAndStart();
        }

        private void ConnectAndStart()
        {
            _client = new ARFlowClient($"http://{m_serverAddress}");

            float originalFx = 0, originalFy = 0;
            float principalPointX = 0, principalPointY = 0;
            int cameraResolutionX = _res.x;
            int cameraResolutionY = _res.y;

            if (_usingMainCamera && _mainCam)
            {
                Matrix4x4 P = _mainCam.projectionMatrix;
                originalFx = P[0,0] * cameraResolutionX;
                originalFy = P[1,1] * cameraResolutionY;
                principalPointX = cameraResolutionX * 0.5f;
                principalPointY = cameraResolutionY * 0.5f;
            }
            else
            {
                var eyeToUse = PassthroughCameraEye.Left; 
                if (m_webCamTextureManager != null) {
                    if (System.Enum.IsDefined(typeof(PassthroughCameraEye), m_webCamTextureManager.Eye.ToString()))
                    {
                        eyeToUse = (PassthroughCameraEye)System.Enum.Parse(typeof(PassthroughCameraEye), m_webCamTextureManager.Eye.ToString());
                    }
                    else
                    {
                        Debug.LogWarning($"[ARFlowMetaQuest] m_webCamTextureManager.Eye ('{m_webCamTextureManager.Eye}') is not a valid PassthroughCameraEye. Defaulting to Left.");
                    }
                }

                PassthroughCameraIntrinsics intrinsics = PassthroughCameraUtils.GetCameraIntrinsics(eyeToUse);

                if (intrinsics.Resolution.x > 0 && intrinsics.Resolution.y > 0)
                {
                    originalFx = intrinsics.FocalLength.x;
                    originalFy = intrinsics.FocalLength.y;
                    principalPointX = intrinsics.PrincipalPoint.x;
                    principalPointY = intrinsics.PrincipalPoint.y;
                    cameraResolutionX = intrinsics.Resolution.x;
                    cameraResolutionY = intrinsics.Resolution.y;
                    _res = new Vector2Int(cameraResolutionX, cameraResolutionY);
                }
                else
                {
                    Debug.LogError("[ARFlowMetaQuest] Failed to get valid camera intrinsics from PassthroughCameraUtils.");
                }
            }

            Debug.Log($"[ARFlowMetaQuest] Camera Intrinsics: fx={originalFx:F2}, fy={originalFy:F2}, " +
                      $"cx={principalPointX:F2}, cy={principalPointY:F2}, " + 
                      $"resolution={cameraResolutionX}x{cameraResolutionY}");

            int scaledWidth = Mathf.Max(1, Mathf.RoundToInt(cameraResolutionX * m_renderScale));
            int scaledHeight = Mathf.Max(1, Mathf.RoundToInt(cameraResolutionY * m_renderScale));

            _client.Connect(new RegisterRequest
            {
                DeviceName = m_deviceName,
                CameraIntrinsics = new RegisterRequest.Types.CameraIntrinsics
                {
                    FocalLengthX    = originalFx * m_renderScale,
                    FocalLengthY    = originalFy * m_renderScale,
                    ResolutionX     = scaledWidth,
                    ResolutionY     = scaledHeight,
                    PrincipalPointX = principalPointX * m_renderScale,
                    PrincipalPointY = principalPointY * m_renderScale
                },
                CameraColor = new RegisterRequest.Types.CameraColor
                {
                    Enabled       = true,
                    DataType      = "RGB24",
                    ResizeFactorX = 1.0f,
                    ResizeFactorY = 1.0f
                },
                CameraDepth     = new RegisterRequest.Types.CameraDepth     { Enabled = false },
                CameraTransform = new RegisterRequest.Types.CameraTransform { Enabled = true }
            });

            Log($"Connected to {m_serverAddress}");
            _streaming = true;
        }

        private void Update()
        {
            if (!_streaming || _client == null || _isProcessingFrame) return;
            _ = UploadFrameAsync();

            // Handle hand pose logging
            if (m_enableHandPoseLogging && m_handTrackingManager != null)
            {
                _frameCountForHandLogging++;
                if (_frameCountForHandLogging >= m_handPoseLogInterval)
                {
                    LogHandPoses();
                    _frameCountForHandLogging = 0;
                }
            }

            _intervalTimer += Time.unscaledDeltaTime;
            if (_intervalTimer >= 1.0f)
            {
                _uploadFps = _framesSentInInterval / _intervalTimer;
                Debug.Log($"[ARFlowMetaQuest] Client Sending FPS: {_uploadFps:F1}");
                _framesSentInInterval = 0;
                _intervalTimer -= 1.0f;
            }

            if (m_statsText != null)
            {
                string statsText = $"FPS: {_uploadFps:F1} | Latency: {_latencyMs:F1} ms";
                if (m_enableHandPoseLogging && m_handTrackingManager != null)
                {
                    bool leftTracked = m_handTrackingManager.IsLeftHandTracked();
                    bool rightTracked = m_handTrackingManager.IsRightHandTracked();
                    statsText += $"\nHands: L:{(leftTracked ? "✓" : "✗")} R:{(rightTracked ? "✓" : "✗")}";
                }
                m_statsText.text = statsText;
            }
        }

        private async Task UploadFrameAsync()
        {
            if (_isProcessingFrame) return;
            _isProcessingFrame = true;

            try
            {
                Pose cameraPose = new Pose();

                if (_usingMainCamera)
                {
                    if (_mainCam == null || _mainRT == null) 
                    {
                        _isProcessingFrame = false; return;
                    }
                    _mainCam.Render();
                    await ReadRTToTextureAsync(_mainRT);
                    cameraPose.position = _mainCam.transform.position;
                    cameraPose.rotation = _mainCam.transform.rotation;
                }
                else
                {
                    var camTex = m_webCamTextureManager?.WebCamTexture;
                    if (camTex == null || !camTex.didUpdateThisFrame)
                    {
                        _isProcessingFrame = false;
                        return;
                    }
                    await CopyWebCamToTextureAsync(camTex);

                    var eyeToUse = PassthroughCameraEye.Left; 
                    if (m_webCamTextureManager != null)
                    {
                        if (System.Enum.IsDefined(typeof(PassthroughCameraEye), m_webCamTextureManager.Eye.ToString()))
                        {
                            eyeToUse = (PassthroughCameraEye)System.Enum.Parse(typeof(PassthroughCameraEye), m_webCamTextureManager.Eye.ToString());
                        }
                        else
                        {
                            Debug.LogWarning($"[ARFlowMetaQuest] m_webCamTextureManager.Eye ('{m_webCamTextureManager.Eye}') is not a valid PassthroughCameraEye for pose. Defaulting to Left.");
                        }
                    }
                    
                    if (PassthroughCameraUtils.IsSupported)
                    {
                        cameraPose = PassthroughCameraUtils.GetCameraPoseInWorld(eyeToUse);
                    }
                    else
                    {
                        Debug.LogWarning("[ARFlowMetaQuest] Passthrough Camera API not supported, cannot get camera pose.");
                    }
                }

                // Log hand poses along with camera pose (less frequently to avoid spam)
                if (m_enableHandPoseLogging && m_handTrackingManager != null && _framesSentInInterval % 10 == 0)
                {
                    LogDetailedHandPoses(cameraPose);
                }

                float startTime = Time.realtimeSinceStartup;
                if (_cpuTex != null && _cpuTex.isReadable)
                {
                    Matrix4x4 matrix = Matrix4x4.TRS(cameraPose.position, cameraPose.rotation, Vector3.one);

                    float[] poseData = new float[12];
                    // Row 1
                    poseData[0] = matrix.m00;
                    poseData[1] = matrix.m01;
                    poseData[2] = matrix.m02;
                    poseData[3] = matrix.m03;
                    // Row 2
                    poseData[4] = matrix.m10;
                    poseData[5] = matrix.m11;
                    poseData[6] = matrix.m12;
                    poseData[7] = matrix.m13;
                    // Row 3
                    poseData[8] = matrix.m20;
                    poseData[9] = matrix.m21;
                    poseData[10] = matrix.m22;
                    poseData[11] = matrix.m23;

                    byte[] transformBytes = new byte[poseData.Length * sizeof(float)];
                    System.Buffer.BlockCopy(poseData, 0, transformBytes, 0, transformBytes.Length);

                    byte[] handTrackingData = SerializeHandTrackingData();

                    await _client.SendFrameAsync(new DataFrameRequest
                    {
                        Color = ByteString.CopyFrom(_cpuTex.GetRawTextureData()),
                        Transform = ByteString.CopyFrom(transformBytes),
                        Depth = ByteString.CopyFrom(handTrackingData)
                    });
                    _latencyMs = (Time.realtimeSinceStartup - startTime) * 1000.0f;
                    _framesSentInInterval++;
                }
                else
                {
                    Debug.LogWarning("_cpuTex is not ready for sending.");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ARFlowMetaQuest] Error in UploadFrameAsync: {e.Message}");
            }
            finally
            {
                _isProcessingFrame = false;
            }
        }

        private void LogHandPoses()
        {
            if (m_handTrackingManager == null) return;

            bool leftTracked = m_handTrackingManager.IsLeftHandTracked();
            bool rightTracked = m_handTrackingManager.IsRightHandTracked();

            string logMessage = "[ARFlowMetaQuest] Hand Poses - ";
            
            if (leftTracked)
            {
                Vector3 leftIndexTip = m_handTrackingManager.GetLeftIndexTip();
                Vector3 leftThumbTip = m_handTrackingManager.GetLeftThumbTip();
                bool leftPinching = m_handTrackingManager.IsLeftHandPinching();
                bool leftGrabbing = m_handTrackingManager.IsLeftHandGrabbing();
                
                logMessage += $"Left: Index({leftIndexTip.x:F3},{leftIndexTip.y:F3},{leftIndexTip.z:F3}) " +
                             $"Thumb({leftThumbTip.x:F3},{leftThumbTip.y:F3},{leftThumbTip.z:F3}) " +
                             $"Pinch:{leftPinching} Grab:{leftGrabbing} | ";
            }
            else
            {
                logMessage += "Left: Not Tracked | ";
            }

            if (rightTracked)
            {
                Vector3 rightIndexTip = m_handTrackingManager.GetRightIndexTip();
                Vector3 rightThumbTip = m_handTrackingManager.GetRightThumbTip();
                bool rightPinching = m_handTrackingManager.IsRightHandPinching();
                bool rightGrabbing = m_handTrackingManager.IsRightHandGrabbing();
                
                logMessage += $"Right: Index({rightIndexTip.x:F3},{rightIndexTip.y:F3},{rightIndexTip.z:F3}) " +
                             $"Thumb({rightThumbTip.x:F3},{rightThumbTip.y:F3},{rightThumbTip.z:F3}) " +
                             $"Pinch:{rightPinching} Grab:{rightGrabbing}";
            }
            else
            {
                logMessage += "Right: Not Tracked";
            }

            Debug.Log(logMessage);
        }

        private void LogDetailedHandPoses(Pose cameraPose)
        {
            if (m_handTrackingManager == null) return;

            bool leftTracked = m_handTrackingManager.IsLeftHandTracked();
            bool rightTracked = m_handTrackingManager.IsRightHandTracked();

            if (!leftTracked && !rightTracked) return;

            string logMessage = $"[ARFlowMetaQuest] Frame Poses - Camera: Pos({cameraPose.position.x:F3},{cameraPose.position.y:F3},{cameraPose.position.z:F3}) " +
                               $"Rot({cameraPose.rotation.x:F3},{cameraPose.rotation.y:F3},{cameraPose.rotation.z:F3},{cameraPose.rotation.w:F3}) | ";

            if (leftTracked)
            {
                // Get all finger positions for left hand
                Vector3 leftThumb = m_handTrackingManager.GetFingerTipPosition(true, OVRHand.HandFinger.Thumb);
                Vector3 leftIndex = m_handTrackingManager.GetFingerTipPosition(true, OVRHand.HandFinger.Index);
                Vector3 leftMiddle = m_handTrackingManager.GetFingerTipPosition(true, OVRHand.HandFinger.Middle);
                Vector3 leftRing = m_handTrackingManager.GetFingerTipPosition(true, OVRHand.HandFinger.Ring);
                Vector3 leftPinky = m_handTrackingManager.GetFingerTipPosition(true, OVRHand.HandFinger.Pinky);
                
                float leftPinchStrength = m_handTrackingManager.GetLeftPinchStrength();
                var leftConfidence = m_handTrackingManager.GetLeftHandConfidence();

                logMessage += $"LeftHand: Confidence:{leftConfidence} PinchStr:{leftPinchStrength:F2} " +
                             $"Fingers[T:({leftThumb.x:F2},{leftThumb.y:F2},{leftThumb.z:F2}) " +
                             $"I:({leftIndex.x:F2},{leftIndex.y:F2},{leftIndex.z:F2}) " +
                             $"M:({leftMiddle.x:F2},{leftMiddle.y:F2},{leftMiddle.z:F2}) " +
                             $"R:({leftRing.x:F2},{leftRing.y:F2},{leftRing.z:F2}) " +
                             $"P:({leftPinky.x:F2},{leftPinky.y:F2},{leftPinky.z:F2})] | ";
            }

            if (rightTracked)
            {
                // Get all finger positions for right hand
                Vector3 rightThumb = m_handTrackingManager.GetFingerTipPosition(false, OVRHand.HandFinger.Thumb);
                Vector3 rightIndex = m_handTrackingManager.GetFingerTipPosition(false, OVRHand.HandFinger.Index);
                Vector3 rightMiddle = m_handTrackingManager.GetFingerTipPosition(false, OVRHand.HandFinger.Middle);
                Vector3 rightRing = m_handTrackingManager.GetFingerTipPosition(false, OVRHand.HandFinger.Ring);
                Vector3 rightPinky = m_handTrackingManager.GetFingerTipPosition(false, OVRHand.HandFinger.Pinky);
                
                float rightPinchStrength = m_handTrackingManager.GetRightPinchStrength();
                var rightConfidence = m_handTrackingManager.GetRightHandConfidence();

                logMessage += $"RightHand: Confidence:{rightConfidence} PinchStr:{rightPinchStrength:F2} " +
                             $"Fingers[T:({rightThumb.x:F2},{rightThumb.y:F2},{rightThumb.z:F2}) " +
                             $"I:({rightIndex.x:F2},{rightIndex.y:F2},{rightIndex.z:F2}) " +
                             $"M:({rightMiddle.x:F2},{rightMiddle.y:F2},{rightMiddle.z:F2}) " +
                             $"R:({rightRing.x:F2},{rightRing.y:F2},{rightRing.z:F2}) " +
                             $"P:({rightPinky.x:F2},{rightPinky.y:F2},{rightPinky.z:F2})]";
            }

            Debug.Log(logMessage);
        }

        // Hand tracking event handlers
        private void OnLeftHandTrackingChanged(bool isTracked)
        {
            if (_lastLeftHandTracked != isTracked)
            {
                Debug.Log($"[ARFlowMetaQuest] Left hand tracking changed: {isTracked}");
                _lastLeftHandTracked = isTracked;
            }
        }

        private void OnRightHandTrackingChanged(bool isTracked)
        {
            if (_lastRightHandTracked != isTracked)
            {
                Debug.Log($"[ARFlowMetaQuest] Right hand tracking changed: {isTracked}");
                _lastRightHandTracked = isTracked;
            }
        }

        // Public method to get current hand poses (for external access)
        public (Vector3 leftIndex, Vector3 rightIndex, bool leftTracked, bool rightTracked) GetCurrentHandPoses()
        {
            if (m_handTrackingManager == null)
                return (Vector3.zero, Vector3.zero, false, false);

            return (
                m_handTrackingManager.GetLeftIndexTip(),
                m_handTrackingManager.GetRightIndexTip(),
                m_handTrackingManager.IsLeftHandTracked(),
                m_handTrackingManager.IsRightHandTracked()
            );
        }

        // Public method to get detailed hand data for external systems
        public HandPoseData GetDetailedHandData()
        {
            if (m_handTrackingManager == null)
                return new HandPoseData();

            return new HandPoseData
            {
                LeftHandTracked = m_handTrackingManager.IsLeftHandTracked(),
                RightHandTracked = m_handTrackingManager.IsRightHandTracked(),
                LeftIndexTip = m_handTrackingManager.GetLeftIndexTip(),
                LeftThumbTip = m_handTrackingManager.GetLeftThumbTip(),
                RightIndexTip = m_handTrackingManager.GetRightIndexTip(),
                RightThumbTip = m_handTrackingManager.GetRightThumbTip(),
                LeftPinching = m_handTrackingManager.IsLeftHandPinching(),
                RightPinching = m_handTrackingManager.IsRightHandPinching(),
                LeftGrabbing = m_handTrackingManager.IsLeftHandGrabbing(),
                RightGrabbing = m_handTrackingManager.IsRightHandGrabbing(),
                LeftPinchStrength = m_handTrackingManager.GetLeftPinchStrength(),
                RightPinchStrength = m_handTrackingManager.GetRightPinchStrength(),
                LeftConfidence = m_handTrackingManager.GetLeftHandConfidence(),
                RightConfidence = m_handTrackingManager.GetRightHandConfidence()
            };
        }

        /// <summary>
        /// Serializes hand tracking data into a byte array for network transmission via Depth field
        /// </summary>
        private byte[] SerializeHandTrackingData()
        {
            if (m_handTrackingManager == null || !m_enableHandPoseLogging)
                return new byte[0];

            var handData = GetDetailedHandData();
            
            // Create a structured binary format for hand tracking data
            // Format: [Header(4)] [LeftHand Data] [RightHand Data] [Timestamp(8)]
            var dataList = new List<byte>();
            
            // Header: "HAND" (4 bytes) to identify this as hand tracking data
            dataList.AddRange(System.Text.Encoding.ASCII.GetBytes("HAND"));
            
            // Left Hand Data (if tracked)
            dataList.Add((byte)(handData.LeftHandTracked ? 1 : 0));
            if (handData.LeftHandTracked)
            {
                // Left hand finger positions (5 fingers * 3 floats * 4 bytes = 60 bytes)
                AddVector3ToBytes(dataList, m_handTrackingManager.GetFingerTipPosition(true, OVRHand.HandFinger.Thumb));
                AddVector3ToBytes(dataList, m_handTrackingManager.GetFingerTipPosition(true, OVRHand.HandFinger.Index));
                AddVector3ToBytes(dataList, m_handTrackingManager.GetFingerTipPosition(true, OVRHand.HandFinger.Middle));
                AddVector3ToBytes(dataList, m_handTrackingManager.GetFingerTipPosition(true, OVRHand.HandFinger.Ring));
                AddVector3ToBytes(dataList, m_handTrackingManager.GetFingerTipPosition(true, OVRHand.HandFinger.Pinky));
                
                // Left hand state data (8 bytes)
                AddFloatToBytes(dataList, handData.LeftPinchStrength);
                dataList.Add((byte)(handData.LeftPinching ? 1 : 0));
                dataList.Add((byte)(handData.LeftGrabbing ? 1 : 0));
                dataList.Add((byte)handData.LeftConfidence);
                dataList.Add(0); // padding
            }
            
            // Right Hand Data (if tracked)
            dataList.Add((byte)(handData.RightHandTracked ? 1 : 0));
            if (handData.RightHandTracked)
            {
                // Right hand finger positions (5 fingers * 3 floats * 4 bytes = 60 bytes)
                AddVector3ToBytes(dataList, m_handTrackingManager.GetFingerTipPosition(false, OVRHand.HandFinger.Thumb));
                AddVector3ToBytes(dataList, m_handTrackingManager.GetFingerTipPosition(false, OVRHand.HandFinger.Index));
                AddVector3ToBytes(dataList, m_handTrackingManager.GetFingerTipPosition(false, OVRHand.HandFinger.Middle));
                AddVector3ToBytes(dataList, m_handTrackingManager.GetFingerTipPosition(false, OVRHand.HandFinger.Ring));
                AddVector3ToBytes(dataList, m_handTrackingManager.GetFingerTipPosition(false, OVRHand.HandFinger.Pinky));
                
                // Right hand state data (8 bytes)
                AddFloatToBytes(dataList, handData.RightPinchStrength);
                dataList.Add((byte)(handData.RightPinching ? 1 : 0));
                dataList.Add((byte)(handData.RightGrabbing ? 1 : 0));
                dataList.Add((byte)handData.RightConfidence);
                dataList.Add(0); // padding
            }
            
            // Timestamp (8 bytes)
            var timestamp = System.BitConverter.GetBytes(Time.realtimeSinceStartup);
            dataList.AddRange(timestamp);
            
            return dataList.ToArray();
        }

        /// <summary>
        /// Helper method to add Vector3 data to byte list
        /// </summary>
        private void AddVector3ToBytes(List<byte> dataList, Vector3 vector)
        {
            dataList.AddRange(System.BitConverter.GetBytes(vector.x));
            dataList.AddRange(System.BitConverter.GetBytes(vector.y));
            dataList.AddRange(System.BitConverter.GetBytes(vector.z));
        }

        /// <summary>
        /// Helper method to add float data to byte list
        /// </summary>
        private void AddFloatToBytes(List<byte> dataList, float value)
        {
            dataList.AddRange(System.BitConverter.GetBytes(value));
        }

        private async Task CopyWebCamToTextureAsync(WebCamTexture camTex)
        {
            CopyMarker.Begin();
            var rt = RenderTexture.GetTemporary(camTex.width, camTex.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(camTex, rt);
            await ReadRTToTextureAsync(rt);
            RenderTexture.ReleaseTemporary(rt);
            CopyMarker.End();
        }

        private async Task ReadRTToTextureAsync(RenderTexture rt)
        {
            if (rt == null || rt.width == 0 || rt.height == 0) {
                 Debug.LogError("[ARFlowMetaQuest] RenderTexture is null or invalid dimensions.");
                 return;
            }

            int scaledWidth = Mathf.Max(1, Mathf.RoundToInt(rt.width * m_renderScale));
            int scaledHeight = Mathf.Max(1, Mathf.RoundToInt(rt.height * m_renderScale));

            if (_cpuTex == null || _cpuTex.width != scaledWidth || _cpuTex.height != scaledHeight || _cpuTex.format != TextureFormat.RGB24)
            {
                if (_cpuTex != null) Destroy(_cpuTex);
                _cpuTex = new Texture2D(scaledWidth, scaledHeight, TextureFormat.RGB24, false);
            }

            RenderTexture scaledRT = RenderTexture.GetTemporary(scaledWidth, scaledHeight, 0, rt.format);
            Graphics.Blit(rt, scaledRT);

            _gpuReadbackTCS = new TaskCompletionSource<bool>();
            AsyncGPUReadback.Request(scaledRT, 0, TextureFormat.RGB24, OnCompleteReadback);
            
            RenderTexture.ReleaseTemporary(scaledRT);

            await _gpuReadbackTCS.Task;
        }

        void OnCompleteReadback(AsyncGPUReadbackRequest request)
        {
            if (request.hasError)
            {
                Debug.LogError("[ARFlowMetaQuest] GPU readback error!");
                _gpuReadbackTCS?.TrySetResult(false);
                return;
            }

            if (_cpuTex != null && _cpuTex.width == request.width && _cpuTex.height == request.height)
            {
                _cpuTex.LoadRawTextureData(request.GetData<byte>());
                _cpuTex.Apply(false);
                _gpuReadbackTCS?.TrySetResult(true);
            }
            else
            {
                Debug.LogError("[ARFlowMetaQuest] _cpuTex is null, or dimensions mismatch after GPU readback.");
                _gpuReadbackTCS?.TrySetResult(false);
            }
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR
            if (_usingMainCamera && _mainCam != null) _mainCam.targetTexture = null;
#endif
            _streaming = false;
            
            // Unsubscribe from hand tracking events
            if (m_handTrackingManager != null)
            {
                m_handTrackingManager.OnLeftHandTrackingChanged -= OnLeftHandTrackingChanged;
                m_handTrackingManager.OnRightHandTrackingChanged -= OnRightHandTrackingChanged;
            }
            
            if (_cpuTex != null) Destroy(_cpuTex);
            if (_mainRT != null) 
            {
                if (RenderTexture.active == _mainRT) RenderTexture.active = null;
                _mainRT.Release();
                Destroy(_mainRT);
            }
        }

        private void Log(string msg)
        {
            Debug.Log(msg);
            if (m_debugText) m_debugText.text = msg;
        }
    }

    // Data structure for detailed hand pose information
    [System.Serializable]
    public struct HandPoseData
    {
        public bool LeftHandTracked;
        public bool RightHandTracked;
        public Vector3 LeftIndexTip;
        public Vector3 LeftThumbTip;
        public Vector3 RightIndexTip;
        public Vector3 RightThumbTip;
        public bool LeftPinching;
        public bool RightPinching;
        public bool LeftGrabbing;
        public bool RightGrabbing;
        public float LeftPinchStrength;
        public float RightPinchStrength;
        public OVRHand.TrackingConfidence LeftConfidence;
        public OVRHand.TrackingConfidence RightConfidence;
    }
}
