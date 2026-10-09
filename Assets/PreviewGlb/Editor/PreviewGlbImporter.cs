using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GLTFast;
using GLTFast.Export;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.Experimental;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.Pipeline.Preview.Glb
{
    // Exports one asset as a self-contained glTF-Binary (.glb) for web 3D viewers, emitted as the
    // "glb" artifact. A non-primary ScriptedImporter: nothing imports with it by default; Project
    // Service runs it on demand by type name:
    //   T:{guid}+Unity.Pipeline.Preview.Glb.PreviewGlbImporter, artifact "glb".
    // Prefabs and models export their hierarchy; a material is shown on a sphere. A glTF is platform
    // neutral, so any environment can produce it. glTF only knows PBR materials: URP Lit and Unlit
    // convert well, custom shaders fall back to their base color and texture.
    [ScriptedImporter(Version, "___preview_glb___", AllowCaching = true)]
    public sealed class PreviewGlbImporter : ScriptedImporter
    {
        public const int Version = 2;
        public const string ArtifactName = "glb";

        // Unity's importer version tracking does not reliably reach non-primary artifacts.
        const string VersionDependency = "Unity.Pipeline.Preview.Glb/PreviewGlbImporter.Version";

        [InitializeOnLoadMethod]
        static void RegisterVersionDependency()
        {
            // Workers receive the main editor's custom dependencies.
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;
            AssetDatabase.RegisterCustomDependency(VersionDependency, Hash128.Compute(Version.ToString()));
        }

        public override void OnImportAsset(AssetImportContext ctx)
        {
            ctx.DependsOnCustomDependency(VersionDependency);
            // Cooked-artifact bytes are deduplicated, so a self-dependency alone misses changes
            // through prefab -> material -> texture chains.
            ctx.DependsOnArtifact(ctx.assetPath);
            foreach (var dependency in AssetDatabase.GetDependencies(ctx.assetPath, recursive: true))
            {
                if (dependency != ctx.assetPath)
                    ctx.DependsOnArtifact(dependency);
            }

            var asset = AssetDatabase.LoadMainAssetAtPath(ctx.assetPath);
            if (asset == null)
            {
                ctx.LogImportError($"Asset not found: {ctx.assetPath}");
                return;
            }

            var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".glb");
            try
            {
                if (!ExportToFile(asset, tempPath, out var error))
                {
                    // A console error, not ctx.LogImportError: import workers report any import
                    // message as "had errors", and an asset that can't be exported is not a failure
                    // of the import itself. The missing artifact tells the caller there is no glb.
                    Debug.LogError($"PreviewGlbImporter: {ctx.assetPath}: {error}");
                    return;
                }
#if UNITY_6000_5_OR_NEWER
                ctx.SetOutputArtifactFile(ArtifactName, tempPath);
#else
                File.Copy(tempPath, ctx.GetOutputArtifactFilePath(ArtifactName), overwrite: true);
#endif
                Debug.Log($"PreviewGlbImporter: wrote {new FileInfo(tempPath).Length} bytes for {ctx.assetPath}");
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        // Builds a temporary instance of the asset and exports it to a .glb at `path`.
        static bool ExportToFile(Object asset, string path, out string error)
        {
            GameObject instance;
            switch (asset)
            {
                case GameObject gameObject:
                    instance = (GameObject)Object.Instantiate(gameObject);
                    break;
                case Material material:
                    instance = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    instance.GetComponent<Renderer>().sharedMaterial = material;
                    break;
                default:
                    error = $"{asset.GetType().Name} can't be previewed in 3D";
                    return false;
            }

            var meshCopies = new List<Mesh>();
            var materialCopies = new List<Material>();
            try
            {
                instance.name = asset.name;
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                instance.SetActive(true);
                MakeMeshesReadable(instance, meshCopies);
                ApproximateCustomMaterials(instance, materialCopies);

                var export = new GameObjectExport(new ExportSettings
                {
                    Format = GltfFormat.Binary,
                    ImageDestination = ImageDestination.MainBuffer,
                    FileConflictResolution = FileConflictResolution.Overwrite,
                    // Lights and cameras would only fight the viewer's own.
                    ComponentMask = ComponentType.Mesh,
                });
                if (!export.AddScene(new[] { instance }, asset.name))
                {
                    error = "glTFast could not add the asset to the scene";
                    return false;
                }

                if (!SaveSync(export, path))
                {
                    error = "glTFast failed to write the glb";
                    return false;
                }
                BlendCutoutMaterials(path);

                error = null;
                return true;
            }
            finally
            {
                Object.DestroyImmediate(instance);
                foreach (var mesh in meshCopies)
                    Object.DestroyImmediate(mesh);
                foreach (var material in materialCopies)
                    Object.DestroyImmediate(material);
            }
        }

        // glTFast converts URP, built-in Standard and its own glTF shaders. Any other shader would
        // export untextured, so swap in a URP Lit stand-in carrying its likely base texture and color.
        static void ApproximateCustomMaterials(GameObject root, List<Material> copies)
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null)
                return;
            var standIns = new Dictionary<Material, Material>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                var materials = renderer.sharedMaterials;
                for (var i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    if (material == null || IsConvertible(material.shader))
                        continue;
                    if (!standIns.TryGetValue(material, out var standIn))
                    {
                        standIn = new Material(lit) { name = material.name };
                        var texture = FindBaseTexture(material);
                        if (texture != null)
                            standIn.SetTexture("_BaseMap", texture);
                        foreach (var colorName in new[] { "_BaseColor", "_Color", "_MainColor", "_Tint" })
                        {
                            if (material.HasColor(colorName))
                            {
                                standIn.SetColor("_BaseColor", material.GetColor(colorName));
                                break;
                            }
                        }
                        standIns[material] = standIn;
                        copies.Add(standIn);
                    }
                    materials[i] = standIn;
                }
                renderer.sharedMaterials = materials;
            }
        }

        static bool IsConvertible(Shader shader) =>
            shader != null && (shader.name.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal)
                || shader.name.StartsWith("Standard", StringComparison.Ordinal)
                || shader.name.StartsWith("Unlit/", StringComparison.Ordinal)
                || shader.name.StartsWith("glTF", StringComparison.Ordinal)
                || shader.name.StartsWith("Shader Graphs/glTF", StringComparison.Ordinal));

        static readonly string[] k_BaseTextureHints = { "base", "main", "albedo", "diffuse", "color", "sample", "tex" };
        static readonly string[] k_NotBaseTextureHints = { "ramp", "normal", "bump", "mask", "occlusion", "metal", "rough", "spec", "height", "detail", "emission", "lightmap", "shadow", "noise" };

        // The shader's texture most likely to be its base color: the first texture property whose
        // name suggests a base map, skipping ramps, normal maps, masks and the like.
        static Texture FindBaseTexture(Material material)
        {
            var shader = material.shader;
            Texture fallback = null;
            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture)
                    continue;
                var name = shader.GetPropertyName(i).ToLowerInvariant();
                var texture = material.GetTexture(shader.GetPropertyName(i));
                if (!(texture is Texture2D) || Array.Exists(k_NotBaseTextureHints, name.Contains))
                    continue;
                if (Array.Exists(k_BaseTextureHints, name.Contains))
                    return texture;
                fallback ??= texture;
            }
            return fallback;
        }

        // glTFast reads a non-readable mesh back from the GPU asynchronously, which crashes the GL
        // device of an import worker. The editor can still read such a mesh's arrays in edit mode, so
        // export a readable copy built from them.
        static void MakeMeshesReadable(GameObject root, List<Mesh> copies)
        {
            Mesh Readable(Mesh mesh)
            {
                if (mesh == null || mesh.isReadable)
                    return mesh;
                var copy = CopyMesh(mesh);
                copies.Add(copy);
                return copy;
            }

            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(includeInactive: true))
                filter.sharedMesh = Readable(filter.sharedMesh);
            foreach (var skinned in root.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true))
                skinned.sharedMesh = Readable(skinned.sharedMesh);
        }

        static Mesh CopyMesh(Mesh source)
        {
            var copy = new Mesh { name = source.name, indexFormat = source.indexFormat };
            copy.SetVertices(source.vertices);
            copy.SetNormals(source.normals);
            copy.SetTangents(source.tangents);
            copy.SetColors(source.colors32);
            var uvs = new List<Vector4>();
            for (var channel = 0; channel < 8; channel++)
            {
                source.GetUVs(channel, uvs);
                if (uvs.Count > 0)
                    copy.SetUVs(channel, uvs);
            }
            copy.boneWeights = source.boneWeights;
            copy.bindposes = source.bindposes;
            copy.subMeshCount = source.subMeshCount;
            for (var subMesh = 0; subMesh < source.subMeshCount; subMesh++)
                copy.SetIndices(source.GetIndices(subMesh), source.GetTopology(subMesh), subMesh);
            copy.bounds = source.bounds;
            return copy;
        }

        // The Unity Dashboard's web viewer ignores glTF's alphaMode MASK and draws alpha-clipped
        // materials (leaves, grass, fences) as opaque quads. It does honor BLEND, so cut-out
        // materials are exported as blended ones. Remove this once the viewer supports MASK.
        static void BlendCutoutMaterials(string glbPath)
        {
            var bytes = File.ReadAllBytes(glbPath);
            var jsonLength = BitConverter.ToInt32(bytes, 12);
            var json = Encoding.UTF8.GetString(bytes, 20, jsonLength);
            if (!json.Contains("\"alphaMode\":\"MASK\""))
                return;

            json = json.Replace("\"alphaMode\":\"MASK\"", "\"alphaMode\":\"BLEND\"");
            json = Regex.Replace(json, ",\"alphaCutoff\":[-+0-9.eE]+|\"alphaCutoff\":[-+0-9.eE]+,", "");
            var newJson = Encoding.UTF8.GetBytes(json.TrimEnd(' '));
            var padded = (newJson.Length + 3) & ~3;
            var rest = bytes.Length - 20 - jsonLength;

            using var stream = new MemoryStream(12 + 8 + padded + rest);
            using var writer = new BinaryWriter(stream);
            writer.Write(0x46546C67); // "glTF"
            writer.Write(2);
            writer.Write(12 + 8 + padded + rest);
            writer.Write(padded);
            writer.Write(0x4E4F534A); // "JSON"
            writer.Write(newJson);
            for (var i = newJson.Length; i < padded; i++)
                writer.Write((byte)' ');
            writer.Write(bytes, 20 + jsonLength, rest);
            File.WriteAllBytes(glbPath, stream.ToArray());
        }

        // An import must finish synchronously. glTFast's public save is async, but its writer has a
        // synchronous path (the one its own editor menu uses) that completes without yielding.
        static bool SaveSync(GameObjectExport export, string path)
        {
            var writer = typeof(GameObjectExport)
                .GetField("m_Writer", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(export);
            var save = writer?.GetType().GetMethod(
                "SaveToFileAndDisposeInternal", BindingFlags.Instance | BindingFlags.NonPublic);
            if (save == null)
                throw new MissingMethodException("glTFast's synchronous save is gone; check the glTFast version");

            var task = (Task<bool>)save.Invoke(writer, new object[] { path, true });
            if (!task.IsCompleted)
                throw new InvalidOperationException("glTFast's synchronous save did not complete synchronously");
            return task.Result;
        }

        /// <summary>Produce the glb for an asset in this editor and copy it to a file (for testing).</summary>
        public static bool ProduceToFile(string assetPath, string outputPath)
        {
            var guid = AssetDatabase.GUIDFromAssetPath(assetPath);
            if (guid.Empty())
            {
                Debug.LogError($"PreviewGlbImporter: asset not found: {assetPath}");
                return false;
            }
            // Picks up a changed importer version, so a stale cached artifact isn't reused.
            AssetDatabase.Refresh();
            var artifactId = AssetDatabaseExperimental.ProduceArtifact(new ArtifactKey(guid, typeof(PreviewGlbImporter)));
            if (!artifactId.isValid || !AssetDatabaseExperimental.GetArtifactPaths(artifactId, out var paths))
            {
                Debug.LogError($"PreviewGlbImporter: no artifact for {assetPath}");
                return false;
            }
            foreach (var artifactPath in paths)
            {
                if (!artifactPath.EndsWith("." + ArtifactName, StringComparison.OrdinalIgnoreCase))
                    continue;
                FileUtil.ReplaceFile(artifactPath, outputPath);
                Debug.Log($"PreviewGlbImporter: {assetPath} -> {outputPath} ({new FileInfo(outputPath).Length} bytes)");
                return true;
            }
            Debug.LogError($"PreviewGlbImporter: the import of {assetPath} produced no glb");
            return false;
        }

        /// <summary>Batchmode entry: -previewAsset &lt;assetPath&gt; -previewOut &lt;file.glb&gt;.</summary>
        public static void ProduceFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            string Arg(string name)
            {
                var index = Array.IndexOf(args, name);
                return index >= 0 && index < args.Length - 1 ? args[index + 1] : null;
            }
            var ok = ProduceToFile(Arg("-previewAsset"), Arg("-previewOut"));
            EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
