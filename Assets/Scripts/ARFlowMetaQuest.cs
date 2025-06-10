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
        [SerializeField] private Text     m_connectedIPText;
        [SerializeField] private Text     m_recordingStatusText;
        [SerializeField] private Text     m_recordingDurationText;
        
        [Header("Control Buttons")]
        [SerializeField] private Button   m_recordingToggleButton;
        [SerializeField] private Button   m_reconnectButton;

        [Header("ARFlow connection")]
        [Tooltip("gRPC endpoint, e.g. 192.168.1.189:8500")]
        [SerializeField] private string   m_serverAddress;
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
        [SerializeField] private int m_handPoseLogInterval = 1000;

        private ARFlowClient _client;
        private Texture2D    _cpuTex;
        private Vector2Int   _res;

        private bool   _streaming;
        private float  _latencyMs;
        private int    _framesSentInInterval;
        private float  _intervalTimer;
        private float  _uploadFps;
        private bool   _isProcessingFrame;
        
        // Recording state tracking
        private float  _recordingStartTime;
        private bool   _isRecording;

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

            while (m_webCamTextureManager.WebCamTexture == null ||
                   m_webCamTextureManager.WebCamTexture.width <= 16)
            {
                yield return null;
            }

            var camTex = m_webCamTextureManager.WebCamTexture;
            _res = new Vector2Int(camTex.width, camTex.height);

            if (m_previewImage) m_previewImage.texture = camTex;
            Debug.Log($"WebCam ready {_res.x}×{_res.y}");

            ConnectAndStart();
        }

        private void ConnectAndStart()
        {
            _client = new ARFlowClient($"http://{m_serverAddress}");

            float originalFx = 0, originalFy = 0;
            float principalPointX = 0, principalPointY = 0;
            int cameraResolutionX = _res.x;
            int cameraResolutionY = _res.y;

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

            Debug.Log($"Connected to {m_serverAddress}");
            _streaming = true;
            _isRecording = true;
            _recordingStartTime = Time.realtimeSinceStartup;
            
            // Update debug text to show connected IP address
            UpdateConnectedIPText($"Connected to: {m_serverAddress}");
            UpdateRecordingStatus();
            
            // Setup button listeners and initial states
            SetupButtons();
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
            
            // Update recording duration
            UpdateRecordingDuration();
        }

        private async Task UploadFrameAsync()
        {
            if (_isProcessingFrame) return;
            _isProcessingFrame = true;

            try
            {
                Pose cameraPose = new Pose();

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

            if (!leftTracked && !rightTracked) return;

            string logMessage = "[ARFlowMetaQuest] Hand Poses - ";
            
            if (leftTracked)
            {
                var leftPoints = m_handTrackingManager.GetAllHandPoints(true);
                int leftPointCount = leftPoints.Count;
                
                logMessage += $"Left: Points:{leftPointCount} | Points: ";
                foreach (Vector3 point in leftPoints)
                {
                    logMessage += $"({point.x:F3}, {point.y:F3}, {point.z:F3}), ";
                }
                logMessage += " | ";
            }
            else
            {
                logMessage += "Left: Not Tracked | ";
            }

            if (rightTracked)
            {
                var rightPoints = m_handTrackingManager.GetAllHandPoints(false);
                int rightPointCount = rightPoints.Count;
                
                logMessage += $"Right: Points:{rightPointCount} | Points: ";
                foreach (Vector3 point in rightPoints)
                {
                    logMessage += $"({point.x:F3}, {point.y:F3}, {point.z:F3}), ";
                }
            }
            else
            {
                logMessage += "Right: Not Tracked";
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

        // Public method to get detailed hand data for external systems
        public HandPoseData GetDetailedHandData()
        {
            if (m_handTrackingManager == null)
                return new HandPoseData();

            return new HandPoseData
            {
                LeftHandTracked = m_handTrackingManager.IsLeftHandTracked(),
                RightHandTracked = m_handTrackingManager.IsRightHandTracked(),
                LeftHandPoints = m_handTrackingManager.GetLeftHandPoints(),
                RightHandPoints = m_handTrackingManager.GetRightHandPoints(),
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
                // Number of points for left hand
                var leftPoints = handData.LeftHandPoints;
                dataList.AddRange(System.BitConverter.GetBytes(leftPoints.Count));
                
                // All bone positions for left hand
                foreach (var point in leftPoints)
                {
                    AddVector3ToBytes(dataList, point);
                }
                
                // Left hand confidence
                dataList.Add((byte)handData.LeftConfidence);
                dataList.Add(0); // padding
                dataList.Add(0); // padding
                dataList.Add(0); // padding
            }
            
            // Right Hand Data (if tracked)
            dataList.Add((byte)(handData.RightHandTracked ? 1 : 0));
            if (handData.RightHandTracked)
            {
                // Number of points for right hand
                var rightPoints = handData.RightHandPoints;
                dataList.AddRange(System.BitConverter.GetBytes(rightPoints.Count));
                
                // All bone positions for right hand
                foreach (var point in rightPoints)
                {
                    AddVector3ToBytes(dataList, point);
                }
                
                // Right hand confidence
                dataList.Add((byte)handData.RightConfidence);
                dataList.Add(0); // padding
                dataList.Add(0); // padding
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
            _streaming = false;
            _isRecording = false;
            
            // Update UI to reflect stopped state
            UpdateRecordingStatus();
            
            // Clean up button listeners
            if (m_recordingToggleButton != null) m_recordingToggleButton.onClick.RemoveAllListeners();
            if (m_reconnectButton != null) m_reconnectButton.onClick.RemoveAllListeners();
            
            // Unsubscribe from hand tracking events
            if (m_handTrackingManager != null)
            {
                m_handTrackingManager.OnLeftHandTrackingChanged -= OnLeftHandTrackingChanged;
                m_handTrackingManager.OnRightHandTrackingChanged -= OnRightHandTrackingChanged;
            }
            
            if (_cpuTex != null) Destroy(_cpuTex);
        }

        private void UpdateConnectedIPText(string text)
        {
            if (m_connectedIPText) m_connectedIPText.text = text;
        }

        private void UpdateRecordingStatus()
        {
            if (m_recordingStatusText)
            {
                m_recordingStatusText.text = _isRecording ? "Recording" : "Not Recording";
            }
            UpdateButtonStates();
        }

        private void UpdateRecordingDuration()
        {
            if (m_recordingDurationText)
            {
                float currentDuration = Time.realtimeSinceStartup - _recordingStartTime;
                m_recordingDurationText.text = $"{currentDuration:F2} seconds";
            }
        }

        /// <summary>
        /// Toggle recording state (useful for UI buttons)
        /// </summary>
        public void ToggleRecording()
        {
            _isRecording = !_isRecording;
            if (_isRecording)
            {
                _recordingStartTime = Time.realtimeSinceStartup;
                Debug.Log("[ARFlowMetaQuest] Recording started manually.");
            }
            else
            {
                Debug.Log("[ARFlowMetaQuest] Recording stopped manually.");
            }
            UpdateRecordingStatus();
        }

        private void SetupButtons()
        {
            // Setup recording control buttons
            if (m_recordingToggleButton != null)
            {
                m_recordingToggleButton.onClick.RemoveAllListeners();
                m_recordingToggleButton.onClick.AddListener(ToggleRecording);
            }
            
            if (m_reconnectButton != null)
            {
                m_reconnectButton.onClick.RemoveAllListeners();
                m_reconnectButton.onClick.AddListener(ReconnectToServer);
            }
            
            // Update button states
            UpdateButtonStates();
        }
        
        private void UpdateButtonStates()
        {
            // Update recording toggle button state and text
            if (m_recordingToggleButton != null)
            {
                m_recordingToggleButton.interactable = true; // Always interactable for toggling
                
                // Update button text based on current recording state
                var buttonText = m_recordingToggleButton.GetComponentInChildren<Text>();
                if (buttonText != null)
                {
                    buttonText.text = _isRecording ? "Stop Recording" : "Start Recording";
                }
                
                // Alternative: Try TextMeshProUGUI if Text component is not found
                if (buttonText == null)
                {
                    var tmpText = m_recordingToggleButton.GetComponentInChildren<TextMeshProUGUI>();
                    if (tmpText != null)
                    {
                        tmpText.text = _isRecording ? "Stop Recording" : "Start Recording";
                    }
                }
            }
                
            // Reconnect button is always available when streaming
            if (m_reconnectButton != null)
                m_reconnectButton.interactable = true;
        }
        
        /// <summary>
        /// Reconnect to ARFlow server - called by UI button
        /// </summary>
        public void ReconnectToServer()
        {
            Debug.Log("[ARFlowMetaQuest] Reconnecting to server...");
            
            // Stop current streaming and reset state
            _streaming = false;
            _isRecording = false;
            
            // Update UI to show disconnected state
            UpdateConnectedIPText("Reconnecting...");
            UpdateRecordingStatus();
            UpdateButtonStates();
            
            // Disconnect current client if exists
            if (_client != null)
            {
                try
                {
                    _client = null; // Let GC handle cleanup
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[ARFlowMetaQuest] Error during disconnect: {e.Message}");
                }
            }
            
            // Restart connection after a brief delay
            StartCoroutine(DelayedReconnect());
        }
        
        private IEnumerator DelayedReconnect()
        {
            // Wait a moment before reconnecting
            yield return new WaitForSeconds(1.0f);
            
            try
            {
                ConnectAndStart();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ARFlowMetaQuest] Reconnection failed: {e.Message}");
                UpdateConnectedIPText("Reconnection failed");
                UpdateButtonStates();
            }
        }
    }

    // Data structure for detailed hand pose information
    [System.Serializable]
    public struct HandPoseData
    {
        public bool LeftHandTracked;
        public bool RightHandTracked;
        public List<Vector3> LeftHandPoints;
        public List<Vector3> RightHandPoints;
        public OVRHand.TrackingConfidence LeftConfidence;
        public OVRHand.TrackingConfidence RightConfidence;
    }
}
