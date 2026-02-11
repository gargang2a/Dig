#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.IO;

/// <summary>
/// 에디터 메뉴에서 고해상도 원형 스프라이트를 생성한다.
/// Tools → Generate Circle Sprite 클릭 1회만 하면 됨.
/// </summary>
public static class CircleSpriteGenerator
{
    [MenuItem("Tools/Generate Circle Sprite")]
    public static void Generate()
    {
        int size = 256;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float center = size * 0.5f;
        float radius = center - 2f; // 안티앨리어스 여유

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist = Mathf.Sqrt((x - center) * (x - center) +
                    (y - center) * (y - center));

                if (dist < radius - 1f)
                    tex.SetPixel(x, y, Color.white);
                else if (dist < radius)
                    tex.SetPixel(x, y, new Color(1, 1, 1, radius - dist)); // AA
                else
                    tex.SetPixel(x, y, Color.clear);
            }
        }

        tex.Apply();

        string dir = "Assets/DigWar/02_Sprites";
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        string path = $"{dir}/Circle256.png";
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);

        AssetDatabase.Refresh();

        // Import 설정 자동 적용
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.filterMode = FilterMode.Bilinear;
            importer.SaveAndReimport();
        }

        Debug.Log($"[CircleSpriteGenerator] 생성 완료: {path}");
    }
}
#endif
