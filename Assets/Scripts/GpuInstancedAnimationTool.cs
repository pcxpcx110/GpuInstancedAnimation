#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class GpuInstancedAnimationTool
{
    [MenuItem("Assets/Generate GpuInstancedAnimation")]
    private static void Generate()
    {
        var targetObject = Selection.activeGameObject;
        if (targetObject == null)
        {
            EditorUtility.DisplayDialog("Warning", "Selected object type is not gameobject.", "OK");
            return;
        }
        var export = targetObject.GetComponent<GpuInstancedAnimationBoneExport>();
        var skinnedMeshRenderers = targetObject.GetComponentsInChildren<SkinnedMeshRenderer>();
        if (!skinnedMeshRenderers.Any() || skinnedMeshRenderers.Count() != 1)
        {
            EditorUtility.DisplayDialog("Warning", "Selected object does not have one skinnedMeshRenderer.", "OK");
            return;
        }

        string prefabName = targetObject.name;

        var selectionPath = Path.GetDirectoryName(AssetDatabase.GetAssetPath(targetObject));
        var skinnedMeshRenderer = skinnedMeshRenderers.First();
        var clips = GetAnimationClips(targetObject);
        if(clips == null)
        {
            EditorUtility.DisplayDialog("Warning", "Selected object has no AnimationClip.", "OK");
            return;
        }

        Directory.CreateDirectory(Path.Combine(selectionPath, prefabName));

        var animationTexture = GenerateAnimationTexture(targetObject, clips, skinnedMeshRenderer);
        AssetDatabase.CreateAsset(animationTexture, string.Format("{0}/{1}/{2}_AnimationTexture.asset", selectionPath, prefabName,prefabName));

        var mesh = GenerateUvBoneWeightedMesh(skinnedMeshRenderer, export);
        AssetDatabase.CreateAsset(mesh, string.Format("{0}/{1}/{2}_Mesh.asset", selectionPath, prefabName,prefabName));

        var material = GenerateMaterial( skinnedMeshRenderer, animationTexture,skinnedMeshRenderer.bones.Length);
        AssetDatabase.CreateAsset(material, string.Format("{0}/{1}/{2}_Material.asset", selectionPath, prefabName, prefabName));

        var go = GenerateMeshRendererObject(targetObject, mesh, material, clips, skinnedMeshRenderer);
        PrefabUtility.SaveAsPrefabAsset(go,string.Format("{0}/{1}/{2}.prefab", selectionPath, prefabName,prefabName), out bool result);

        Object.DestroyImmediate(go);
    }

    private static IEnumerable<AnimationClip> GetAnimationClips(GameObject targetObject)
    {
        var animator = targetObject.GetComponent<Animator>();
        if(animator == null)
        {
            animator = targetObject.GetComponentInChildren<Animator>();
        }
        if(animator )
        {
            if(animator.runtimeAnimatorController)
            {
                return animator.runtimeAnimatorController.animationClips;
            }
            else
            {   
                return null;
            }
        }
        var animation = targetObject.GetComponent<Animation>();
        if(animation== null)
        {
            animation = targetObject.GetComponentInChildren<Animation>();
        }
        if(animation)
        {
            List<AnimationClip> clips = new List<AnimationClip>();
            foreach(AnimationState state in animation)
            {
                clips.Add(state.clip);
            }
            return clips;
        }
        return null;
    }

    private static Mesh GenerateUvBoneWeightedMesh(SkinnedMeshRenderer smr,GpuInstancedAnimationBoneExport export)
    {
        var mesh = Object.Instantiate(smr.sharedMesh);

        var boneSets = smr.sharedMesh.boneWeights;
        var boneIndexes = boneSets.Select(x => new Vector4(x.boneIndex0, x.boneIndex1, x.boneIndex2, x.boneIndex3)).ToList();
        var boneWeights = boneSets.Select(x => new Vector4(x.weight0, x.weight1, x.weight2, x.weight3)).ToList();

        mesh.SetUVs(2, boneIndexes);
        mesh.SetUVs(3, boneWeights);
        if(export!=null)
        {
            var boneHeights = boneSets.Select(x => new Vector4(export.GetBoneHeightWeight( x.boneIndex0), export.GetBoneHeightWeight(x.boneIndex1), export.GetBoneHeightWeight(x.boneIndex2), export.GetBoneHeightWeight(x.boneIndex3))).ToList();
            mesh.SetUVs(4, boneHeights);

        }

        return mesh;
    }

    private static Texture GenerateAnimationTexture(GameObject targetObject, IEnumerable<AnimationClip> clips, SkinnedMeshRenderer smr)
    {
        var textureBoundary = GetCalculatedTextureBoundary(clips, smr.bones.Count());
        var texture = new Texture2D((int)textureBoundary.x, (int)textureBoundary.y, TextureFormat.RGBAHalf, false, true);
        var pixels = texture.GetPixels();
        var pixelIndex = 0;
        //Setup 0 to bindPoses
        foreach (var boneMatrix in smr.bones.Select((b, idx) => b.localToWorldMatrix * smr.sharedMesh.bindposes[idx]))
        {
            pixels[pixelIndex++] = new Color(boneMatrix.m00, boneMatrix.m01, boneMatrix.m02, boneMatrix.m03);
            pixels[pixelIndex++] = new Color(boneMatrix.m10, boneMatrix.m11, boneMatrix.m12, boneMatrix.m13);
            pixels[pixelIndex++] = new Color(boneMatrix.m20, boneMatrix.m21, boneMatrix.m22, boneMatrix.m23);
        }
        foreach (var clip in clips)
        {
            var totalFrames = (int)(clip.length * GpuInstancedAnimation.TargetFrameRate);
            foreach (var frame in Enumerable.Range(0, totalFrames))
            {
                clip.SampleAnimation(targetObject, (float)frame / GpuInstancedAnimation.TargetFrameRate);

                foreach (var boneMatrix in smr.bones.Select((b, idx) => b.localToWorldMatrix * smr.sharedMesh.bindposes[idx]))
                {
                    pixels[pixelIndex++] = new Color(boneMatrix.m00, boneMatrix.m01, boneMatrix.m02, boneMatrix.m03);
                    pixels[pixelIndex++] = new Color(boneMatrix.m10, boneMatrix.m11, boneMatrix.m12, boneMatrix.m13);
                    pixels[pixelIndex++] = new Color(boneMatrix.m20, boneMatrix.m21, boneMatrix.m22, boneMatrix.m23);
                }
            }
        }
        texture.SetPixels(pixels);
        texture.Apply();
        texture.filterMode = FilterMode.Point;
        return texture;
    }

   

    private static Vector2 GetCalculatedTextureBoundary(IEnumerable<AnimationClip> clips, int boneLength)
    {
        var boneMatrixCount = GpuInstancedAnimation.BoneMatrixRowCount * boneLength;

        var totalPixels = clips.Aggregate(boneMatrixCount, (pixels, currentClip) => pixels + boneMatrixCount * (int)(currentClip.length * GpuInstancedAnimation.TargetFrameRate));

        var textureWidth = 1;
        var textureHeight = 1;

        while (textureWidth * textureHeight < totalPixels)
        {
            if (textureWidth <= textureHeight)
            {
                textureWidth *= 2;
            }
            else
            {
                textureHeight *= 2;
            }
        }

        return new Vector2(textureWidth, textureHeight);
    }

    private static Material GenerateMaterial( SkinnedMeshRenderer smr, Texture texture, int boneLength)
    {
        var material = Object.Instantiate(smr.sharedMaterial);
        material.shader = Shader.Find("AnimationGpuInstancing/Standard");
        material.SetTexture("_AnimTex", texture);
        material.SetInt("_PixelCountPerFrame", GpuInstancedAnimation.BoneMatrixRowCount * boneLength);
        material.enableInstancing = true;

        return material;
    }

    private static GameObject GenerateMeshRendererObject(GameObject targetObject, Mesh mesh, Material material, IEnumerable<AnimationClip> clips, SkinnedMeshRenderer smr)
    {
        var go = new GameObject();
        go.name = targetObject.name;

        var animation = go.AddComponent<GpuInstancedAnimation>();

        GpuInstancedAnimationBoneExport animationBoneExport = targetObject.GetComponent<GpuInstancedAnimationBoneExport>();
        if (animationBoneExport != null && animationBoneExport.bones != null && animationBoneExport.bones.Count > 0)
        {
            List<GpuInstancedAnimationBone> animationBones = new List<GpuInstancedAnimationBone>();
            List<GpuInstancedAnimationBoneFrame> animationBoneFrames = new List<GpuInstancedAnimationBoneFrame>();
            for (int i = 0; i < smr.bones.Length; ++i)
            {
                var bone = smr.bones[i];
                for (int j = 0; j < animationBoneExport.bones.Count; ++j)
                {
                    if (animationBoneExport.bones[j] == bone)
                    {
                        animationBones.Add(new GpuInstancedAnimationBone { boneName = bone.gameObject.name, index = j, blendWeight = animationBoneExport.GetBoneHeightWeight(j) });
                        animation.boneFrames.Add(new GpuInstancedAnimationBoneFrame
                        {
                            localPosition = targetObject.transform.InverseTransformPoint(bone.position),
                            localForward = targetObject.transform.InverseTransformVector(bone.forward),
                        });
                    }
                }
            }

            animation.bones = animationBones;
            animation.boneFrames = animationBoneFrames;
        }

        var animationClips = new List<GpuInstancedAnimationClip>();
        var currentClipFrames = 0;

        foreach (var clip in clips)
        {
            var frameCount = (int)(clip.length * GpuInstancedAnimation.TargetFrameRate);
            var startFrame = currentClipFrames + 1;
            var endFrame = startFrame + frameCount - 1;


            animationClips.Add(new GpuInstancedAnimationClip(clip.name, startFrame, endFrame, frameCount, clip.isLooping ? GpuInstancedAnimationClip.WrapMode.Loop : GpuInstancedAnimationClip.WrapMode.Once));

            currentClipFrames = endFrame;

            if (animationBoneExport != null && animationBoneExport.bones != null && animationBoneExport.bones.Count > 0)
            {
                var totalFrames = (int)(clip.length * GpuInstancedAnimation.TargetFrameRate);
                foreach (var frame in Enumerable.Range(0, totalFrames))
                {
                    clip.SampleAnimation(targetObject, (float)frame / GpuInstancedAnimation.TargetFrameRate);

                    for (int i = 0; i < smr.bones.Length; ++i)
                    {
                        var bone = smr.bones[i];
                        for (int j = 0; j < animationBoneExport.bones.Count; ++j)
                        {
                            if (animationBoneExport.bones[j] == bone)
                            {

                                animation.boneFrames.Add(new GpuInstancedAnimationBoneFrame
                                {
                                    localPosition = targetObject.transform.InverseTransformPoint(bone.position),
                                    localForward = targetObject.transform.InverseTransformVector(bone.forward),
                                });
                            }
                        }
                    }
                }
            }
        }

        animation.mesh = mesh;
        animation.material = material;
        animation.animationClips = animationClips;

        return go;
    }
}
#endif