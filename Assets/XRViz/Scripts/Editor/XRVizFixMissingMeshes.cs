using UnityEngine;
using UnityEditor;

public static class XRVizFixMissingMeshes
{
    [MenuItem("XRViz/Fix Missing Meshes")]
    public static void Run()
    {
        var root = Selection.activeGameObject;
        if (root == null)
        {
            Debug.LogWarning("Select a top‐level GameObject first.");
            return;
        }

        int fixedCount = 0;

        // 1) Fix MeshFilters
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh != null) continue;
            if (TryFindMeshByName(mf.gameObject.name, out var mesh))
            {
                Undo.RecordObject(mf, "Assign Missing Mesh");
                mf.sharedMesh = mesh;
                fixedCount++;
                Debug.Log($"[XRViz] Set MeshFilter ▶ {mf.gameObject.GetPath()} ← “{mesh.name}”");
            }
            else
            {
                Debug.LogWarning($"[XRViz] MeshFilter ▶ {mf.gameObject.GetPath()} → no asset named “{mf.gameObject.name}”");
            }
        }

        // 2) Fix MeshColliders
        foreach (var mc in root.GetComponentsInChildren<MeshCollider>(true))
        {
            if (mc.sharedMesh != null) continue;
            if (TryFindMeshByName(mc.gameObject.name, out var mesh))
            {
                Undo.RecordObject(mc, "Assign Missing Collider Mesh");
                mc.sharedMesh = mesh;
                fixedCount++;
                Debug.Log($"[XRViz] Set MeshCollider ▶ {mc.gameObject.GetPath()} ← “{mesh.name}”");
            }
            else
            {
                Debug.LogWarning($"[XRViz] MeshCollider ▶ {mc.gameObject.GetPath()} → no asset named “{mc.gameObject.name}”");
            }
        }

        Debug.Log($"[XRViz] Fix Missing Meshes complete: {fixedCount} assignments made.");
    }

    // Searches the AssetDatabase for a Mesh asset matching exactly this name
    static bool TryFindMeshByName(string meshName, out Mesh result)
    {
        result = null;
        var guids = AssetDatabase.FindAssets($"t:Mesh {meshName}");
        foreach (var g in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(g);
            var m = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (m != null && m.name == meshName)
            {
                result = m;
                return true;
            }
        }
        return false;
    }

    // Helper to get full hierarchy path
    static string GetPath(this GameObject go)
    {
        return go.transform.parent == null
            ? go.name
            : go.transform.parent.gameObject.GetPath() + "/" + go.name;
    }
}
