using UnityEditor;
using UnityEngine.XR.Management;

[InitializeOnLoad]
public static class XRLoaderReset
{
    static XRLoaderReset()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        var settings = XRGeneralSettings.Instance;
        var manager = settings != null ? settings.Manager : null;
        if (manager == null)
            return;

        if (state == PlayModeStateChange.ExitingPlayMode && manager.isInitializationComplete)
        {
            manager.DeinitializeLoader();
        }
        else if (state == PlayModeStateChange.EnteredPlayMode && manager.activeLoader == null)
        {
            manager.InitializeLoaderSync();
        }
    }
}
