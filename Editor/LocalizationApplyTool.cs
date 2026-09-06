using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;

/// <summary>
/// LocalizationAuditTool이 뽑아낸 목록에 키를 채워 넣으면, 그 매핑대로
/// 씬의 TMP 오브젝트에 LocalizedText를 일괄로 붙여주는 도구.
///
/// 입력 = LocalizationMissing_&lt;씬&gt;.csv 의 suggested_key 칸을 채운 파일.
///   category,count,text,suggested_key,sample_path
/// text가 정확히 일치하는 TMP 텍스트를 찾아 LocalizedText + 키를 넣는다.
///
/// 이미 LocalizedText가 붙어 있으면:
///   - 키가 들어 있으면 그대로 둔다 (덮어쓰지 않는다)
///   - 키가 비어 있으면 채워준다. 컴포넌트만 붙이고 Key를 안 넣어둔 오브젝트가 있는데,
///     그냥 건너뛰면 이 도구로는 영영 고칠 수 없어 손으로 하나씩 넣어야 한다
///
/// 건드리지 않는 것:
///   - 스크립트가 값을 넣는 텍스트 (붙이면 서로 덮어쓰며 싸운다 — 점검 도구와 같은 기준)
///
/// 프리팹 인스턴스는 **원본 프리팹**에 붙인다. 인스턴스에 붙이면 오버라이드가 되어
/// 같은 프리팹의 다른 인스턴스와 앞으로 새로 놓을 것들이 번역되지 않는다.
///
/// 사용: Tools > Localization > 키 매핑 CSV로 LocalizedText 일괄 붙이기
///       실행 후 씬 저장(Ctrl+S) 필요. Ctrl+Z로 되돌릴 수 있다(프리팹 수정분 제외).
/// </summary>
public static class LocalizationApplyTool
{
    [MenuItem("Tools/Localization/키 매핑 CSV로 LocalizedText 일괄 붙이기")]
    public static void Apply()
    {
        string path = EditorUtility.OpenFilePanel(
            "키를 채운 LocalizationMissing CSV 선택",
            System.IO.Directory.GetParent(Application.dataPath).FullName, "csv");
        if (string.IsNullOrEmpty(path)) return;

        var mapping = ReadMapping(path, out int rowsWithoutKey);
        if (mapping.Count == 0)
        {
            Debug.LogError($"[번역적용] '{System.IO.Path.GetFileName(path)}' 에서 쓸 수 있는 매핑을 못 찾았다. " +
                           "suggested_key 칸을 채웠는지 확인할 것.");
            return;
        }

        var knownKeys = LoadCsvKeys();
        var texts  = Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var driven = CollectScriptDrivenTexts();

        int added = 0, filledEmpty = 0, skippedDriven = 0, skippedExisting = 0;
        var touchedPrefabs = new HashSet<GameObject>();
        var unknownKeys    = new HashSet<string>();

        // 매핑했는데 씬에서 한 번도 못 찾은 문구를 남겨둔다 — 안 그러면 왜 안 붙었는지 알 길이 없다
        var unmatched = new HashSet<string>(mapping.Keys);

        foreach (var t in texts)
        {
            if (t == null) continue;
            if (!mapping.TryGetValue(t.text.Replace("\r", ""), out string key)) continue;

            unmatched.Remove(t.text.Replace("\r", ""));

            // 스크립트가 채우는 텍스트는 컴포넌트가 붙어 있든 아니든 건드리지 않는다 —
            // 빈 키를 채워버리면 LocalizedText와 스크립트가 같은 자리를 서로 덮어쓴다
            if (driven.Contains(t)) { skippedDriven++; continue; }

            // 컴포넌트만 붙이고 Key를 안 채워둔 오브젝트는 채워준다
            // (이미 키가 들어 있으면 절대 덮어쓰지 않는다)
            var existing = t.GetComponent<LocalizedText>();
            if (existing != null)
            {
                if (!string.IsNullOrEmpty(KeyOf(existing))) { skippedExisting++; continue; }

                Undo.RecordObject(existing, "Fill Localization Key");
                SetKey(existing, key);
                EditorUtility.SetDirty(existing);
                filledEmpty++;

                if (knownKeys != null && !knownKeys.Contains(key)) unknownKeys.Add(key);
                continue;
            }

            if (knownKeys != null && !knownKeys.Contains(key)) unknownKeys.Add(key);

            // 프리팹에서 온 오브젝트면 원본에 붙여 모든 인스턴스가 함께 번역되게 한다
            var target = PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject) ?? t.gameObject;
            bool isPrefabAsset = target != t.gameObject;

            if (target.GetComponent<LocalizedText>() != null) { skippedExisting++; continue; }

            LocalizedText loc = isPrefabAsset
                ? target.AddComponent<LocalizedText>()
                : Undo.AddComponent<LocalizedText>(target);

            SetKey(loc, key);
            EditorUtility.SetDirty(loc);
            added++;

            if (isPrefabAsset) touchedPrefabs.Add(target);
        }

        if (touchedPrefabs.Count > 0) AssetDatabase.SaveAssets();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[번역적용] 매핑 {mapping.Count}종 적용");
        sb.AppendLine($"  붙임                 {added}");
        sb.AppendLine($"    └ 프리팹 원본       {touchedPrefabs.Count}개 (저장 완료 — 모든 인스턴스에 반영)");
        sb.AppendLine($"  빈 키 채움            {filledEmpty}   ← 컴포넌트만 있고 Key가 비어 있던 것");
        sb.AppendLine($"  건너뜀 (이미 있음)    {skippedExisting}");
        sb.AppendLine($"  건너뜀 (스크립트 담당) {skippedDriven}");
        if (rowsWithoutKey > 0)
            sb.AppendLine($"  CSV에서 키가 빈 줄     {rowsWithoutKey}   ← 아직 정하지 않은 문구");
        sb.AppendLine();
        sb.Append("씬 저장 필요 (Ctrl+S). 되돌리려면 Ctrl+Z (프리팹에 붙인 것은 제외).");

        Debug.Log(sb.ToString());

        if (unknownKeys.Count > 0)
            Debug.LogWarning($"[번역적용] localization.csv 에 없는 키를 붙였다 — 화면에 키 문자열이 그대로 뜬다. " +
                             $"CSV에 행을 추가할 것:\n  " + string.Join("\n  ", unknownKeys.OrderBy(k => k)));

        if (unmatched.Count > 0)
            Debug.LogWarning($"[번역적용] 매핑했지만 이 씬에서 같은 문구를 못 찾은 것 {unmatched.Count}종 — " +
                             "다른 씬의 문구이거나, 씬 텍스트가 CSV와 한 글자라도 다르다 " +
                             "(앞뒤 공백·줄바꿈까지 정확히 같아야 한다):\n  " +
                             string.Join("\n  ", unmatched.Select(Visible).OrderBy(s => s)));
    }

    /// <summary>
    /// 고른 폴더의 **프리팹 애셋**에 직접 붙인다.
    ///
    /// 씬용 도구는 열려 있는 씬만 훑으므로, 런타임에만 생성되는 프리팹
    /// (로비 방 목록 행 등 런타임 생성 UI)에는 영영 붙지 않는다. 그것들을 위한 것이다.
    ///
    /// ⚠ 프리팹은 애셋이라 **Ctrl+Z로 되돌아가지 않는다**. 되돌리려면 git을 쓴다.
    /// ⚠ 프리팹 안의 스크립트만 보고 판정하므로, 씬 쪽 스크립트가 채우는 텍스트는 걸러내지 못한다.
    ///    CSV에서 키를 비워 제외해 둘 것.
    /// </summary>
    [MenuItem("Tools/Localization/키 매핑 CSV로 LocalizedText 붙이기 (고른 폴더의 프리팹)")]
    public static void ApplyToPrefabs()
    {
        string path = EditorUtility.OpenFilePanel(
            "키를 채운 LocalizationMissing CSV 선택",
            System.IO.Directory.GetParent(Application.dataPath).FullName, "csv");
        if (string.IsNullOrEmpty(path)) return;

        var mapping = ReadMapping(path, out int rowsWithoutKey);
        if (mapping.Count == 0)
        {
            Debug.LogError("[번역적용] 쓸 수 있는 매핑을 못 찾았다. suggested_key 칸을 확인할 것.");
            return;
        }

        string[] folders = LocalizationAuditTool.SelectedFolders();
        if (folders == null || folders.Length == 0) return;

        string[] guids = AssetDatabase.FindAssets("t:Prefab", folders);

        if (!EditorUtility.DisplayDialog("프리팹에 붙이기",
                $"프리팹 {guids.Length}개에 매핑 {mapping.Count}종을 적용한다.\n" +
                "프리팹은 애셋이라 Ctrl+Z로 되돌아가지 않는다 — git으로 되돌려야 한다.\n\n" +
                "대상 폴더:\n" + string.Join("\n", folders),
                "적용", "취소")) return;

        var knownKeys = LoadCsvKeys();
        int added = 0, filledEmpty = 0, skippedDriven = 0, skippedExisting = 0, changedPrefabs = 0;
        var unknownKeys = new HashSet<string>();
        var unmatched   = new HashSet<string>(mapping.Keys);

        for (int i = 0; i < guids.Length; i++)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
            if (EditorUtility.DisplayCancelableProgressBar(
                    "번역 적용", assetPath, (float)i / guids.Length)) break;

            // 프리팹 애셋을 안전하게 고치는 방법 — 임시로 열어 수정하고 다시 저장한다
            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null) continue;

            bool dirty = false;
            var driven = CollectScriptDrivenTexts(root.GetComponentsInChildren<MonoBehaviour>(true));

            foreach (var t in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if (t == null) continue;
                if (!mapping.TryGetValue(t.text.Replace("\r", ""), out string key)) continue;

                unmatched.Remove(t.text.Replace("\r", ""));

                if (driven.Contains(t)) { skippedDriven++; continue; }

                var existing = t.GetComponent<LocalizedText>();
                if (existing != null)
                {
                    if (!string.IsNullOrEmpty(KeyOf(existing))) { skippedExisting++; continue; }
                    SetKey(existing, key);
                    filledEmpty++;
                }
                else
                {
                    SetKey(t.gameObject.AddComponent<LocalizedText>(), key);
                    added++;
                }

                if (knownKeys != null && !knownKeys.Contains(key)) unknownKeys.Add(key);
                dirty = true;
            }

            if (dirty)
            {
                PrefabUtility.SaveAsPrefabAsset(root, assetPath);
                changedPrefabs++;
            }
            PrefabUtility.UnloadPrefabContents(root);
        }
        EditorUtility.ClearProgressBar();
        AssetDatabase.SaveAssets();

        Debug.Log($"[번역적용] 프리팹 {guids.Length}개 중 {changedPrefabs}개 수정 (저장 완료)\n" +
                  $"  붙임                 {added}\n" +
                  $"  빈 키 채움            {filledEmpty}\n" +
                  $"  건너뜀 (이미 있음)    {skippedExisting}\n" +
                  $"  건너뜀 (스크립트 담당) {skippedDriven}\n" +
                  (rowsWithoutKey > 0 ? $"  CSV에서 키가 빈 줄     {rowsWithoutKey}\n" : ""));

        if (unknownKeys.Count > 0)
            Debug.LogWarning("[번역적용] localization.csv 에 없는 키:\n  " +
                             string.Join("\n  ", unknownKeys.OrderBy(k => k)));

        if (unmatched.Count > 0)
            Debug.LogWarning($"[번역적용] 매핑했지만 이 프리팹들에서 못 찾은 것 {unmatched.Count}종 " +
                             "(씬 쪽 문구이거나 텍스트가 한 글자라도 다르다):\n  " +
                             string.Join("\n  ", unmatched.Select(Visible).OrderBy(s => s)));
    }

    // ── 입력 ──────────────────────────────────────────────────

    /// <summary>text → suggested_key. 키가 빈 줄은 건너뛴다.</summary>
    private static Dictionary<string, string> ReadMapping(string path, out int rowsWithoutKey)
    {
        rowsWithoutKey = 0;
        var map = new Dictionary<string, string>();

        var rows = ParseCsv(System.IO.File.ReadAllText(path));
        if (rows.Count < 2) return map;

        // 헤더에서 열 위치를 찾는다 — 열 순서가 바뀌어도 동작하도록
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int cText = header.IndexOf("text");
        int cKey  = header.IndexOf("suggested_key");

        if (cText < 0 || cKey < 0)
        {
            Debug.LogError("[번역적용] CSV에 text / suggested_key 열이 없다.");
            return map;
        }

        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Count <= cText) continue;

            // 점검 도구와 같은 정규화 — 여러 줄 문구가 CRLF로 저장되면 씬 텍스트(\n)와 영영 안 맞는다
            // (git이 윈도우에서 체크아웃할 때 줄바꿈을 바꿔놓는다)
            string text = row[cText].Replace("\r", "");
            string key  = row.Count > cKey ? row[cKey].Trim() : "";

            if (string.IsNullOrEmpty(text)) continue;
            if (string.IsNullOrEmpty(key)) { rowsWithoutKey++; continue; }

            map[text] = key;
        }
        return map;
    }

    /// <summary>따옴표로 감싼 셀(쉼표·줄바꿈 포함)과 "" 이스케이프를 지원하는 최소 CSV 파서.</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row  = new List<string>();
        var cell = new System.Text.StringBuilder();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];

            if (quoted)
            {
                if (ch != '"') { cell.Append(ch); continue; }

                if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = false;
                continue;
            }

            switch (ch)
            {
                case '"':  quoted = true; break;
                case ',':  row.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(cell.ToString()); cell.Clear();
                    rows.Add(row); row = new List<string>();
                    break;
                default: cell.Append(ch); break;
            }
        }

        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }

        // BOM이 첫 열 이름에 붙어 헤더 검색이 실패하는 것을 막는다
        if (rows.Count > 0 && rows[0].Count > 0) rows[0][0] = rows[0][0].TrimStart('﻿');

        return rows;
    }

    private static HashSet<string> LoadCsvKeys()
    {
        var asset = Resources.Load<TextAsset>("localization");
        if (asset == null) return null;

        var keys = new HashSet<string>();
        bool header = true;
        foreach (string line in asset.text.Split('\n'))
        {
            if (header) { header = false; continue; }

            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            int comma = trimmed.IndexOf(',');
            keys.Add(comma < 0 ? trimmed : trimmed.Substring(0, comma));
        }
        return keys;
    }

    // ── 헬퍼 ──────────────────────────────────────────────────

    /// <summary>안 보이는 차이(줄바꿈·앞뒤 공백)가 원인일 때가 많아 눈에 보이게 찍는다.</summary>
    private static string Visible(string s)
        => $"\"{s.Replace("\n", "\\n").Replace("\t", "\\t")}\"";

    /// <summary>LocalizedText.key는 private이라 SerializedObject로 읽는다.</summary>
    private static string KeyOf(LocalizedText loc)
    {
        using var so = new SerializedObject(loc);
        return so.FindProperty("key")?.stringValue;
    }

    /// <summary>LocalizedText.key는 private이라 SerializedObject로 쓴다.</summary>
    private static void SetKey(LocalizedText loc, string key)
    {
        using var so = new SerializedObject(loc);
        so.FindProperty("key").stringValue = key;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    /// <summary>씬 전체 기준.</summary>
    private static HashSet<TMP_Text> CollectScriptDrivenTexts()
        => CollectScriptDrivenTexts(Object.FindObjectsByType<MonoBehaviour>(
               FindObjectsInactive.Include, FindObjectsSortMode.None));

    /// <summary>점검 도구와 같은 기준 — 직렬화 필드로 참조되는 TMP는 스크립트가 채운다.</summary>
    private static HashSet<TMP_Text> CollectScriptDrivenTexts(MonoBehaviour[] behaviours)
    {
        var result = new HashSet<TMP_Text>();

        foreach (var mb in behaviours)
        {
            if (mb == null || mb is LocalizedText) continue;

            using var so = new SerializedObject(mb);
            var prop = so.GetIterator();

            while (prop.NextVisible(true))
            {
                if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                if (prop.objectReferenceValue is TMP_Text tmp) result.Add(tmp);
            }
        }
        return result;
    }
}
