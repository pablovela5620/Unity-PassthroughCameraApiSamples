// Copyright (c) Meta Platforms, Inc. and affiliates.
// Original Source code from Oculus Starter Samples (https://github.com/oculus-samples/Unity-StarterSamples)

using System;
using System.Collections.Generic;
using System.IO;
using Meta.XR.Samples;
using UnityEngine;

namespace PassthroughCameraSamples.StartScene
{
    // Create menu of all scenes included in the build.
    [MetaCodeSample("PassthroughCameraApiSamples-StartScene")]
    public class StartMenu : MonoBehaviour
    {
        public OVROverlay Overlay;
        public OVROverlay Text;
        public OVRCameraRig VrRig;

        // Store scene information for logging
        private Dictionary<int, Tuple<string, string>> sceneInfo = new Dictionary<int, Tuple<string, string>>();

        private void Start()
        {
            var generalScenes = new List<Tuple<int, string>>();
            var passthroughScenes = new List<Tuple<int, string>>();
            var proControllerScenes = new List<Tuple<int, string>>();

            var n = UnityEngine.SceneManagement.SceneManager.sceneCountInBuildSettings;
            for (var sceneIndex = 1; sceneIndex < n; ++sceneIndex)
            {
                var path = UnityEngine.SceneManagement.SceneUtility.GetScenePathByBuildIndex(sceneIndex);
                var sceneName = Path.GetFileNameWithoutExtension(path);
                
                // Store scene info for logging
                sceneInfo[sceneIndex] = new Tuple<string, string>(sceneName, path);

                if (path.Contains("Passthrough"))
                {
                    passthroughScenes.Add(new Tuple<int, string>(sceneIndex, path));
                    Debug.Log($"[StartMenu] Registered PASSTHROUGH scene {sceneIndex}: '{sceneName}' at path: {path}");
                }
                else if (path.Contains("TouchPro"))
                {
                    proControllerScenes.Add(new Tuple<int, string>(sceneIndex, path));
                    Debug.Log($"[StartMenu] Registered TOUCHPRO scene {sceneIndex}: '{sceneName}' at path: {path}");
                }
                else
                {
                    generalScenes.Add(new Tuple<int, string>(sceneIndex, path));
                    Debug.Log($"[StartMenu] Registered GENERAL scene {sceneIndex}: '{sceneName}' at path: {path}");
                }
            }

            var uiBuilder = DebugUIBuilder.Instance;
            if (passthroughScenes.Count > 0)
            {
                _ = uiBuilder.AddLabel("Passthrough Sample Scenes", DebugUIBuilder.DEBUG_PANE_LEFT);
                Debug.Log($"[StartMenu] Created Passthrough menu section with {passthroughScenes.Count} scenes");
                foreach (var scene in passthroughScenes)
                {
                    _ = uiBuilder.AddButton(Path.GetFileNameWithoutExtension(scene.Item2), () => LoadScene(scene.Item1), -1, DebugUIBuilder.DEBUG_PANE_LEFT);
                }
            }

            if (proControllerScenes.Count > 0)
            {
                _ = uiBuilder.AddLabel("Pro Controller Sample Scenes", DebugUIBuilder.DEBUG_PANE_RIGHT);
                Debug.Log($"[StartMenu] Created Pro Controller menu section with {proControllerScenes.Count} scenes");
                foreach (var scene in proControllerScenes)
                {
                    _ = uiBuilder.AddButton(Path.GetFileNameWithoutExtension(scene.Item2), () => LoadScene(scene.Item1), -1, DebugUIBuilder.DEBUG_PANE_RIGHT);
                }
            }

            _ = uiBuilder.AddLabel("Press ☰ at any time to return to scene selection", DebugUIBuilder.DEBUG_PANE_CENTER);
            if (generalScenes.Count > 0)
            {
                _ = uiBuilder.AddDivider(DebugUIBuilder.DEBUG_PANE_CENTER);
                _ = uiBuilder.AddLabel("Sample Scenes", DebugUIBuilder.DEBUG_PANE_CENTER);
                Debug.Log($"[StartMenu] Created General menu section with {generalScenes.Count} scenes");
                foreach (var scene in generalScenes)
                {
                    _ = uiBuilder.AddButton(Path.GetFileNameWithoutExtension(scene.Item2), () => LoadScene(scene.Item1), -1, DebugUIBuilder.DEBUG_PANE_CENTER);
                }
            }

            uiBuilder.Show();
            Debug.Log("[StartMenu] Menu initialization complete");
        }

        private void LoadScene(int idx)
        {
            DebugUIBuilder.Instance.Hide();
            
            // Enhanced logging with scene details
            if (sceneInfo.ContainsKey(idx))
            {
                var info = sceneInfo[idx];
                Debug.Log($"[StartMenu] 🎬 LOADING SCENE {idx}: '{info.Item1}' from path: {info.Item2}");
                Debug.Log($"[StartMenu] 📁 Scene category: {GetSceneCategory(info.Item2)}");
            }
            else
            {
                Debug.Log($"[StartMenu] ⚠️ Loading scene {idx} (no additional info available)");
            }
            
            UnityEngine.SceneManagement.SceneManager.LoadScene(idx);
        }

        private string GetSceneCategory(string path)
        {
            if (path.Contains("Passthrough"))
                return "PASSTHROUGH";
            else if (path.Contains("TouchPro"))
                return "TOUCHPRO";
            else
                return "GENERAL";
        }
    }
}
