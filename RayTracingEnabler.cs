// ─────────────────────────────────────────────────────────────────────────────
//  RayTracingEnabler.cs   (Unity 2023.2+ / Unity 6 HDRP)
//  One-click + UI окно для включения Ray Tracing
//  Всё в одном файле: настройки, окно, логика применения
// ─────────────────────────────────────────────────────────────────────────────
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

#region ── Перечисления ────────────────────────────────────────────────────────
public enum RayTracingQuality { Low, Medium, High }
public enum RayTracingDenoiser { SpatialTemporal, Temporal }
#endregion

#region ── ScriptableObject – настройки ───────────────────────────────────────
[CreateAssetMenu(menuName = "RayTracing/Settings", fileName = "RayTracingSettings")]
public class RayTracingSettings : ScriptableObject
{
    [Header("Quality")]
    public RayTracingQuality quality = RayTracingQuality.Medium;

    [Header("Effects")]
    public bool reflections   = true;
    public bool globalIllum   = true;
    public bool shadows       = true;
    public bool ambientOccl   = true;

    [Header("Intensity")]
    [Range(0f, 2f)] public float reflIntensity = 1f;
    [Range(0f, 2f)] public float giIntensity   = 1f;
    [Range(0f, 2f)] public float shadIntensity = 1f;
    [Range(0f, 2f)] public float aoIntensity   = 1f;

    // ── Вычисляемые параметры ───────────────────────────────────────────────
    public int Samples => quality switch { RayTracingQuality.Low => 16, RayTracingQuality.Medium => 32, _ => 64 };
    public RayTracingDenoiser Denoiser => quality == RayTracingQuality.Low ? RayTracingDenoiser.SpatialTemporal : RayTracingDenoiser.Temporal;
}
#endregion

#region ── EditorWindow – UI ───────────────────────────────────────────────────
public class RayTracingEnablerWindow : EditorWindow
{
    private RayTracingSettings settings;
    private Vector2 scroll;

    [MenuItem("Tools/Ray Tracing/Open Settings")]
    static void Open() => GetWindow<RayTracingEnablerWindow>("Ray Tracing").Show();

    void OnEnable()
    {
        const string path = "Assets/Editor/RayTracingEnabler/RayTracingSettings.asset";
        settings = AssetDatabase.LoadAssetAtPath<RayTracingSettings>(path);
        if (!settings)
        {
            settings = CreateInstance<RayTracingSettings>();
            AssetDatabase.CreateAsset(settings, path);
            AssetDatabase.SaveAssets();
        }
    }

    void OnGUI()
    {
        GUILayout.Label("Ray Tracing Enabler", EditorStyles.boldLabel);
        scroll = EditorGUILayout.BeginScrollView(scroll);

        settings.quality = (RayTracingQuality)EditorGUILayout.EnumPopup("Quality", settings.quality);
        settings.reflections = EditorGUILayout.Toggle("Ray-Traced Reflections", settings.reflections);
        settings.globalIllum = EditorGUILayout.Toggle("Ray-Traced GI", settings.globalIllum);
        settings.shadows     = EditorGUILayout.Toggle("Ray-Traced Shadows", settings.shadows);
        settings.ambientOccl = EditorGUILayout.Toggle("Ray-Traced AO", settings.ambientOccl);

        settings.reflIntensity = EditorGUILayout.Slider("Reflection Intensity", settings.reflIntensity, 0f, 2f);
        settings.giIntensity   = EditorGUILayout.Slider("GI Intensity",       settings.giIntensity,   0f, 2f);
        settings.shadIntensity = EditorGUILayout.Slider("Shadow Intensity",  settings.shadIntensity, 0f, 2f);
        settings.aoIntensity   = EditorGUILayout.Slider("AO Intensity",      settings.aoIntensity,   0f, 2f);

        EditorGUILayout.EndScrollView();

        if (GUILayout.Button("Apply & Enable Ray Tracing"))
        {
            RayTracingEnabler.Apply(settings);
            Close();
        }

        if (GUILayout.Button("Quick Enable (Medium defaults)"))
        {
            RayTracingEnabler.Apply(null);
            Close();
        }
    }
}
#endregion

#region ── Основная логика ─────────────────────────────────────────────────────
public static class RayTracingEnabler
{
    [MenuItem("Tools/Ray Tracing/Quick Enable", priority = 0)]
    public static void QuickEnable() => Apply(null);

    // ── Публичный API ───────────────────────────────────────────────────────
    public static void Apply(RayTracingSettings user = null)
    {
        // 1. Проверки HDRP
        var hdrp = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
        if (!hdrp)
        {
            EditorUtility.DisplayDialog("Error", "HDRP required (Window → Package Manager → HDRP).", "OK");
            return;
        }

        var global = HDRenderPipelineGlobalSettings.instance;
        if (!global)
        {
            EditorUtility.DisplayDialog("Error", "Create HDRP Global Settings: Edit → Render Pipeline → HDRP Settings.", "OK");
            return;
        }

        // 2. DX12 + отключение static batching (рекомендации Unity 2025)
        if (EditorUserBuildSettings.activeBuildTarget == BuildTargetGroup.Standalone)
        {
            PlayerSettings.SetGraphicsApis(BuildTargetGroup.Standalone, new[] { GraphicsDeviceType.Direct3D12 });
            PlayerSettings.staticBatching = false;
            Debug.Log("DX12 + static batching disabled");
        }

        // 3. Включаем RT в HDRP
        hdrp.supportRayTracing = true;
        hdrp.rayTracing = true;
        global.supportRayTracing = true;

        // 4. Настройки по умолчанию, если пользователь не передал
        if (user == null)
        {
            user = ScriptableObject.CreateInstance<RayTracingSettings>();
            user.quality = RayTracingQuality.Medium;
            user.reflections = user.globalIllum = user.shadows = user.ambientOccl = true;
        }

        // 5. Global Volume
        var go = GameObject.Find("Global Volume") ?? new GameObject("Global Volume");
        var vol = go.GetComponent<Volume>() ?? go.AddComponent<Volume>();
        vol.isGlobal = true;
        vol.priority = 0;
        go.hideFlags = HideFlags.HideInHierarchy;

        var profile = vol.sharedProfile ?? CreateProfile();
        vol.sharedProfile = profile;

        // 6. Overrides
        Configure<RayTracingReflections>(profile, user.reflections, ov =>
        {
            ov.rayMaxIterations.value = user.Samples;
            ov.rayTracing.value = true;
            ov.quality.value = (int)user.quality;
            ov.denoiser.value = (int)user.Denoiser;
            ov.intensity.value = user.reflIntensity;
        });

        Configure<RayTracedGlobalIllumination>(profile, user.globalIllum, ov =>
        {
            ov.rayMaxIterations.value = user.Samples;
            ov.rayTracing.value = true;
            ov.quality.value = (int)user.quality;
            ov.denoiser.value = (int)user.Denoiser;
            ov.intensity.value = user.giIntensity;
        });

        Configure<RayTracedShadows>(profile, user.shadows, ov =>
        {
            ov.rayMaxIterations.value = user.Samples / 2;
            ov.rayTracing.value = true;
            ov.intensity.value = user.shadIntensity;
        });

        Configure<RayTracedAmbientOcclusion>(profile, user.ambientOccl, ov =>
        {
            ov.rayMaxIterations.value = user.Samples;
            ov.rayTracing.value = true;
            ov.quality.value = (int)user.quality;
            ov.intensity.value = user.aoIntensity;
        });

        // 7. Realtime GI
        Lightmapping.realtimeGI = true;

        // 8. Сохранить
        EditorUtility.SetDirty(hdrp);
        EditorUtility.SetDirty(global);
        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        EditorUtility.DisplayDialog("Success", $"Ray Tracing enabled – Quality: {user.quality}", "OK");
        Debug.Log("Ray Tracing полностью настроен");
    }

    // ── Утилиты ───────────────────────────────────────────────────────────────
    static VolumeProfile CreateProfile()
    {
        var p = ScriptableObject.CreateInstance<VolumeProfile>();
        AssetDatabase.CreateAsset(p, "Assets/Editor/RayTracingEnabler/RT_VolumeProfile.asset");
        return p;
    }

    static void Configure<T>(VolumeProfile p, bool enable, Action<T> cfg) where T : VolumeComponent
    {
        if (!p.TryGet(out T comp)) comp = p.Add<T>();
        comp.active = enable;
        if (enable) cfg(comp);
    }
}
#endregion
