using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using System.Linq;
using UnityEditor.SceneManagement;

public static class RayTracingEnabler
{
    private const string MENU_PATH = "Tools/Ray Tracing/";
    private const string VOLUME_NAME = "RTX Global Volume";
    private const string PROFILE_NAME = "RTX Volume Profile";

    [MenuItem(MENU_PATH + "Quick Enable (Medium)", priority = 100)]
    public static void QuickEnable() => EnableRayTracing(RTXPreset.Medium);

    [MenuItem(MENU_PATH + "Open RTX Wizard...", priority = 101)]
    public static void OpenWizard() => RTXWizardWindow.Open();

    [MenuItem(MENU_PATH + "Remove All RTX Settings", priority = 200)]
    public static void RemoveAll()
    {
        if (!EditorUtility.DisplayDialog("Remove RTX", "Удалить ВСЕ настройки Ray Tracing (Volume, Profile, DX12 и т.д.)?", "Да", "Отмена"))
            return;

        Undo.RegisterCompleteObjectUndo(new UnityEngine.Object[] { }, "Remove RTX");
        DisableRayTracing();
        RemoveRTXVolume();
        EditorUtility.DisplayDialog("RTX Enabler", "Все RTX-настройки удалены!", "OK");
    }

    static void EnableRayTracing(RTXPreset preset)
    {
        if (!HDRPCheck())
            return;

        Undo.RegisterCompleteObjectUndo(new UnityEngine.Object[] { }, "Enable RTX");

        SetGraphicsAPI();
        DisableStaticBatching();
        DisableSRPBatcherIfNeeded();

        var volume = GetOrCreateGlobalVolume();
        var profile = GetOrCreateVolumeProfile(volume);

        ApplyPreset(profile, preset);

        SetHDRPAssetRayTracing(profile);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorUtility.DisplayDialog("RTX Enabler", $"Ray Tracing включён на пресете «{preset}»!\nПерезапустите Unity для полной активации.", "OK");
    }

    static bool HDRPCheck()
    {
        if (!RenderPipelineManager.currentPipeline is HDRenderPipeline)
        {
            EditorUtility.DisplayDialog("RTX Enabler", "Проект должен быть на HDRP!", "OK");
            return false;
        }
        if (!SystemInfo.supportsRayTracing)
        {
            EditorUtility.DisplayDialog("RTX Enabler", "Твоя видеокарта не поддерживает Ray Tracing\nНужна NVIDIA RTX или AMD RX 6000+", "OK");
            return false;
        }
        return true;
    }

    static void SetGraphicsAPI()
    {
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.IL2CPP);
        var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
        if (!apis.Contains(GraphicsDeviceType.Direct3D12))
        {
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D12 });
        }
    }

    static void DisableStaticBatching()
    {
        PlayerSettings.SetStaticBatching(false);
    }

    static void DisableSRPBatcherIfNeeded()
    {
        var hdrpAsset = HDRenderPipeline.currentAsset;
        if (hdrpAsset != null && hdrpAsset.renderPipelineSettings.supportSRPBatcher)
        {
            hdrpAsset.renderPipelineSettings.supportSRPBatcher = false;
            EditorUtility.SetDirty(hdrpAsset);
        }
    }

    static Volume GetOrCreateGlobalVolume()
    {
        var existing = Object.FindObjectsOfType<Volume>().FirstOrDefault(v => v.name == VOLUME_NAME && v.isGlobal);
        if (existing != null) return existing;

        var go = new GameObject(VOLUME_NAME) { tag = "EditorOnly" };
        var volume = go.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 0;
        Undo.RegisterCreatedObjectUndo(go, "Create RTX Volume");
        return volume;
    }

    static VolumeProfile GetOrCreateVolumeProfile(Volume volume)
    {
        if (volume.profile == null || volume.profile.name != PROFILE_NAME)
        {
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = PROFILE_NAME;
            AssetDatabase.CreateAsset(profile, "Assets/RTX Volume Profile.asset");
            volume.profile = profile;
            Undo.RegisterCreatedObjectUndo(profile, "Create RTX Profile");
        }
        return volume.profile;
    }

    static void ApplyPreset(VolumeProfile profile, RTXPreset preset)
    {
        profile.components.Clear();

        var quality = preset switch
        {
            RTXPreset.Low => RayTracingQuality.Low,
            RTXPreset.Medium => RayTracingQuality.Medium,
            RTXPreset.High => RayTracingQuality.High,
            RTXPreset.Ultra => RayTracingQuality.Ultra,
            _ => RayTracingQuality.Medium
        };

        AddOverride<RayTracingReflections>(profile, o =>
        {
            o.active = true;
            o.quality.value = quality;
            o.intensityMultiplier.value = preset == RTXPreset.Ultra ? 1.3f : 1f;
        });

        AddOverride<RayTracingShadows>(profile, o =>
        {
            o.active = true;
            o.quality.value = quality;
        });

        AddOverride<RayTracingAmbientOcclusion>(profile, o =>
        {
            o.active = true;
            o.quality.value = quality;
            o.intensity.value = preset == RTXPreset.Ultra ? 1.5f : 1f;
        });

        if (HDRenderPipeline.currentAsset?.renderPipelineSettings.supportRayTracingGI == true)
        {
            AddOverride<RayTracingGlobalIllumination>(profile, o =>
            {
                o.active = true;
                o.quality.value = quality;
            });
        }

        AddOverride<ScreenSpaceReflection>(profile, o => o.active = false);
    }

    static void AddOverride<T>(VolumeProfile profile, System.Action<T> setup) where T : VolumeComponent, new()
    {
        if (profile.TryGet<T>(out var comp))
            profile.components.Remove(comp);

        comp = new T();
        setup(comp);
        profile.components.Add(comp);
    }

    static void SetHDRPAssetRayTracing(VolumeProfile profile)
    {
        var hdrp = HDRenderPipeline.currentAsset;
        if (hdrp != null)
        {
            hdrp.renderPipelineSettings.supportRayTracing = true;
            hdrp.renderPipelineSettings.rayTracing = true;
            EditorUtility.SetDirty(hdrp);
        }
    }

    static void DisableRayTracing()
    {
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        PlayerSettings.SetStaticBatching(true);
        var hdrp = HDRenderPipeline.currentAsset;
        if (hdrp != null)
        {
            hdrp.renderPipelineSettings.supportRayTracing = false;
            hdrp.renderPipelineSettings.rayTracing = false;
        }
    }

    static void RemoveRTXVolume()
    {
        var volume = Object.FindObjectsOfType<Volume>().FirstOrDefault(v => v.name == VOLUME_NAME);
        if (volume != null) Undo.DestroyObjectImmediate(volume.gameObject);

        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/RTX Volume Profile.asset");
        if (profile != null) AssetDatabase.DeleteAsset("Assets/RTX Volume Profile.asset");
    }

    enum RTXPreset { Low, Medium, High, Ultra }
    enum RayTracingQuality { Low = 0, Medium = 1, High = 2, Ultra = 3 }

    private class RTXWizardWindow : EditorWindow
    {
        private RTXPreset preset = RTXPreset.Medium;
        private bool reflections = true, shadows = true, ao = true, gi = true;

        [MenuItem("Tools/Ray Tracing/Open RTX Wizard...")]
        public static void Open() => GetWindow<RTXWizardWindow>("RTX Wizard").Show();

        void OnGUI()
        {
            GUILayout.Label("Unity RTX Enabler 2.0", EditorStyles.boldLabel);
            GUILayout.Space(10);

            preset = (RTXPreset)EditorGUILayout.EnumPopup("Пресет качества", preset);

            GUILayout.Label("Отдельные эффекты:", EditorStyles.boldLabel);
            reflections = EditorGUILayout.Toggle("Ray Traced Reflections", reflections);
            shadows = EditorGUILayout.Toggle("Ray Traced Shadows", shadows);
            ao = EditorGUILayout.Toggle("Ray Traced AO", ao);
            gi = EditorGUILayout.Toggle("Ray Traced GI (Unity 6+)", gi);

            GUILayout.Space(20);

            if (GUILayout.Button("ВКЛЮЧИТЬ RAY TRACING!", GUILayout.Height(40)))
            {
                EnableRayTracing(preset);
                Close();
            }

            if (GUILayout.Button("Удалить всё", GUILayout.Height(30)))
            {
                RemoveAll();
            }
        }
    }
}
