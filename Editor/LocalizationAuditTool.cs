using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 씬·프리팹의 TMP 텍스트가 번역되는 상태인지 점검하는 에디터 툴.
///
/// 텍스트가 화면에 뜨는 경로는 두 가지뿐이다:
///   1. LocalizedText 컴포넌트가 키로 채운다      → 번역됨
///   2. 스크립트가 코드에서 채운다                 → 스크립트 쪽에서 이미 처리했다
/// 둘 다 아니면 인스펙터에 박아둔 문자열이 그대로 나온다 = **번역 누락**.
///
/// 2번을 구분하려고 씬의 모든 MonoBehaviour 직렬화 필드를 훑어
/// 그 TMP_Text를 참조하는 컴포넌트가 있는지 확인한다. 이게 텍스트 검색으로는
/// 알 수 없는 부분이라 이 툴이 필요하다 (안 그러면 statusText 같은 게 전부 오탐이 된다).
///
/// 사용: Tools > Localization > 텍스트 번역 점검 (현재 씬)
///       결과 로그를 클릭하면 해당 오브젝트가 하이라이트된다.
/// </summary>
public static class LocalizationAuditTool
{
    private const string CsvResourcePath = "localization";

    private enum Verdict
    {
        Localized,      // LocalizedText + 정상 키
        BadKey,         // LocalizedText는 있는데 키가 비었거나 표에 없음
        ScriptDriven,   // 스크립트가 값을 넣는다 — 손댈 필요 없음
        Missing,        // 아무도 안 채운다 = 인스펙터 문자열이 그대로 노출된다
        Empty           // 빈 텍스트 (자리표시자) — 무시
    }

    // ── 메뉴 ──────────────────────────────────────────────────

    [MenuItem("Tools/Localization/텍스트 번역 점검 (현재 씬)")]
    public static void AuditScene()
    {
        var keys = LoadCsvKeys();
        if (keys == null) return;

        var texts = Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var driven = CollectScriptDrivenTexts(
            Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None));

        // 씬 이름을 붙여야 씬마다 돌렸을 때 결과 파일이 서로 덮어쓰지 않는다
        string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        Report(string.IsNullOrEmpty(scene) ? "현재 씬" : scene, texts, driven, keys);
    }

    /// <summary>
    /// 고른 폴더 안의 프리팹만 점검한다.
    ///
    /// 프로젝트 전체를 훑으면 TextMesh Pro 예제나 구매한 UI 에셋팩의 프리팹까지 딸려온다.
    /// 남의 에셋에 LocalizedText를 붙이면 그 패키지를 갱신할 때 날아가고, 목록도 못 쓸 만큼 길어진다.
    ///
    /// Project 창에서 폴더를 골라두고 실행하면 그 폴더들만 본다. 아무것도 안 골랐으면 폴더를 물어본다.
    /// </summary>
    [MenuItem("Tools/Localization/텍스트 번역 점검 (고른 폴더의 프리팹)")]
    public static void AuditPrefabs()
    {
        var keys = LoadCsvKeys();
        if (keys == null) return;

        string[] folders = SelectedFolders();
        if (folders == null || folders.Length == 0) return;

        var texts = new List<TMP_Text>();
        var behs  = new List<MonoBehaviour>();

        string[] guids = AssetDatabase.FindAssets("t:Prefab", folders);
        for (int i = 0; i < guids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            if (EditorUtility.DisplayCancelableProgressBar(
                    "번역 점검", path, (float)i / guids.Length)) break;

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null) continue;

            texts.AddRange(root.GetComponentsInChildren<TMP_Text>(true));
            behs.AddRange(root.GetComponentsInChildren<MonoBehaviour>(true));
        }
        EditorUtility.ClearProgressBar();

        Debug.Log($"[번역점검] 프리팹 {guids.Length}개 검사 — 대상 폴더:\n  " + string.Join("\n  ", folders));
        Report("프리팹_" + string.Join("+", folders.Select(f => f.Split('/').Last())),
               texts.ToArray(), CollectScriptDrivenTexts(behs.ToArray()), keys);
    }

    /// <summary>Project 창에서 고른 폴더들. 선택이 없으면 폴더 선택 창을 띄운다 (붙이기 도구도 쓴다).</summary>
    internal static string[] SelectedFolders()
    {
        var picked = Selection.assetGUIDs
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(AssetDatabase.IsValidFolder)
            .ToArray();

        if (picked.Length > 0) return picked;

        string abs = EditorUtility.OpenFolderPanel("점검할 프리팹 폴더 선택", Application.dataPath, "");
        if (string.IsNullOrEmpty(abs)) return null;

        abs = abs.Replace('\\', '/');
        string root = Application.dataPath.Replace('\\', '/');

        if (!abs.StartsWith(root))
        {
            Debug.LogError("[번역점검] Assets 폴더 안에서 골라야 한다.");
            return null;
        }

        return new[] { "Assets" + abs.Substring(root.Length) };
    }

    /// <summary>
    /// Key가 비어 있는 LocalizedText를 제거한다.
    ///
    /// 빈 키는 런타임에 아무것도 하지 않으므로(Refresh가 그냥 반환한다) 화면은 멀쩡하지만,
    /// 컴포넌트가 붙어 있는 한 일괄 붙이기 도구가 "이미 있음"으로 건너뛰고 점검은 계속 오류로 잡는다.
    /// 영어로 두기로 한 문구에 남아 있는 것들을 정리할 때 쓴다.
    ///
    /// ⚠ 나중에 키를 채울 생각이면 지우지 말 것. 되돌리려면 Ctrl+Z.
    /// </summary>
    [MenuItem("Tools/Localization/키가 빈 LocalizedText 제거 (현재 씬)")]
    public static void RemoveEmptyKeyComponents()
    {
        var empties = Object.FindObjectsByType<LocalizedText>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                            .Where(l => string.IsNullOrEmpty(KeyOf(l)))
                            .ToList();

        if (empties.Count == 0)
        {
            Debug.Log("[번역정리] 키가 빈 LocalizedText가 없다.");
            return;
        }

        var lines = empties.Select(l =>
        {
            var tmp = l.GetComponent<TMP_Text>();
            return $"  {Escaped(tmp != null ? tmp.text : "")}  ←  {GetPath(l.transform)}";
        }).ToList();

        if (!EditorUtility.DisplayDialog("키가 빈 LocalizedText 제거",
                $"{empties.Count}개를 제거한다. 이 오브젝트들은 앞으로 번역되지 않는다.\n" +
                "나중에 키를 채울 생각이면 취소할 것.\n\n(Ctrl+Z로 되돌릴 수 있다)",
                "제거", "취소")) return;

        foreach (var l in empties) Undo.DestroyObjectImmediate(l);

        Debug.Log($"[번역정리] 키가 빈 LocalizedText {empties.Count}개 제거. 씬 저장 필요 (Ctrl+S).\n" +
                  string.Join("\n", lines));
    }

    [MenuItem("Tools/Localization/씬에서 쓰지 않는 CSV 키 목록")]
    public static void ListUnusedKeys()
    {
        var keys = LoadCsvKeys();
        if (keys == null) return;

        var used = Object.FindObjectsByType<LocalizedText>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                         .Select(KeyOf)
                         .Where(k => !string.IsNullOrEmpty(k))
                         .ToHashSet();

        var unused = keys.Where(k => !used.Contains(k)).OrderBy(k => k).ToList();

        Debug.Log($"[번역점검] 현재 씬의 LocalizedText가 쓰는 키 {used.Count}개.\n" +
                  $"이 씬에서 안 쓰는 키 {unused.Count}개 (코드에서 Get()으로 부르거나 다른 씬에서 쓸 수 있으니 " +
                  $"바로 지우면 안 된다):\n  " + string.Join("\n  ", unused));
    }

    // ── 점검 ──────────────────────────────────────────────────

    private static void Report(string scope, TMP_Text[] texts, HashSet<TMP_Text> driven, HashSet<string> keys)
    {
        int localized = 0, scriptDriven = 0, empty = 0;
        var missing = new List<TMP_Text>();
        var badKey  = new List<(TMP_Text text, string reason)>();

        foreach (var t in texts)
        {
            if (t == null) continue;

            switch (Judge(t, driven, keys, out string reason))
            {
                case Verdict.Localized:    localized++;    break;
                case Verdict.ScriptDriven: scriptDriven++; break;
                case Verdict.Empty:        empty++;        break;
                case Verdict.BadKey:       badKey.Add((t, reason)); break;
                case Verdict.Missing:      missing.Add(t); break;
            }
        }

        // 현재 텍스트를 함께 찍는다 — 경로만으로는 어떤 문구인지 알 수 없어 키를 정할 수가 없다.
        // 다듬지 않고 그대로 보여준다: 뒤 공백이나 줄바꿈이 하나만 달라도 매칭이 안 되므로
        // 여기서 정리해 찍으면 그 텍스트로 만든 매핑이 조용히 빗나간다
        foreach (var (t, reason) in badKey)
            Debug.LogError($"[번역점검] 키 오류 — {reason}\n" +
                           $"  {Escaped(t.text)}\n  {GetPath(t.transform)}", t);

        // 같은 문구가 여러 오브젝트에 흩어져 있는 경우가 많다 (프리팹 인스턴스, 반복 라벨).
        // 하나씩 경고를 뿌리면 수백 줄이라 못 읽으므로 문구별로 묶는다.
        var groups = missing
            .GroupBy(t => t.text.Replace("\r", ""))
            .Select(g => new { Text = g.Key, Items = g.ToList(), Skip = LooksUntranslatable(g.Key) })
            .OrderByDescending(g => g.Items.Count)
            .ToList();

        var real = groups.Where(g => !g.Skip).ToList();
        var noise = groups.Where(g => g.Skip).ToList();

        // 상위 몇 개만 콘솔에 띄우고 전체는 CSV로 뺀다 (콘솔은 긴 목록을 잘라먹는다)
        foreach (var g in real.Take(TopN))
            Debug.LogWarning($"[번역점검] 번역 누락 ×{g.Items.Count} — \"{Preview(g.Text)}\"\n" +
                             $"  {GetPath(g.Items[0].transform)}", g.Items[0]);

        string csv = WriteMissingCsv(scope, real.Select(g => (g.Text, g.Items)),
                                            noise.Select(g => (g.Text, g.Items)));

        Debug.Log($"[번역점검] {scope}: TMP 텍스트 {texts.Length}개\n" +
                  $"  번역됨(LocalizedText)   {localized}\n" +
                  $"  스크립트가 채움          {scriptDriven}   ← 손댈 필요 없음\n" +
                  $"  빈 텍스트               {empty}\n" +
                  $"  키 오류                 {badKey.Count}\n" +
                  $"  번역 누락               {missing.Count}개 오브젝트 / 서로 다른 문구 {groups.Count}종\n" +
                  $"     └ 번역 대상          {real.Count}종 ({real.Sum(g => g.Items.Count)}개)\n" +
                  $"     └ 번역 불필요 후보    {noise.Count}종 ({noise.Sum(g => g.Items.Count)}개)  숫자·기호·한두 글자\n\n" +
                  $"전체 목록 → {csv}\n" +
                  $"(콘솔에는 많은 순으로 {TopN}종만 띄웠다. 나머지는 CSV 참고)");
    }

    private const int TopN = 20;

    /// <summary>
    /// 번역할 필요가 없어 보이는 문구인지 — 숫자, 기호, 화살표, 한두 글자짜리.
    /// 넘패드 숫자 버튼이나 ▲▼ 같은 것들이 목록을 덮어버리는 것을 막는다.
    /// 어디까지나 분류일 뿐 제외가 아니다. CSV에는 다 들어간다.
    /// </summary>
    private static bool LooksUntranslatable(string s)
    {
        string t = s.Trim();
        if (t.Length <= 2) return true;          // "1", "▲", "OK" 같은 것

        foreach (char c in t)
            if (char.IsLetter(c)) return false;  // 글자가 하나라도 있으면 문구로 본다

        return true;                             // 남은 것은 숫자·기호뿐
    }

    /// <summary>
    /// 전체 목록을 CSV로 남긴다 — 콘솔은 긴 목록을 잘라먹고 복사도 어렵다.
    /// 열: 분류, 개수, 문구, 예시 경로. 이 파일에 키를 채워 넣고 작업하면 된다.
    /// </summary>
    private static string WriteMissingCsv(string scope,
        IEnumerable<(string Text, List<TMP_Text> Items)> real,
        IEnumerable<(string Text, List<TMP_Text> Items)> noise)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("category,count,text,suggested_key,sample_path");

        void Rows(IEnumerable<(string Text, List<TMP_Text> Items)> src, string category)
        {
            foreach (var g in src)
                sb.Append(category).Append(',')
                  .Append(g.Items.Count).Append(',')
                  .Append(Csv(g.Text)).Append(",,")
                  .Append(Csv(GetPath(g.Items[0].transform))).Append('\n');
        }

        Rows(real, "번역대상");
        Rows(noise, "번역불필요후보");

        string safeScope = string.Join("_", scope.Split(System.IO.Path.GetInvalidFileNameChars()));
        string path = System.IO.Path.Combine(
            System.IO.Directory.GetParent(Application.dataPath).FullName,
            $"LocalizationMissing_{safeScope}.csv");

        System.IO.File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(true));
        return path;
    }

    /// <summary>쉼표·따옴표·줄바꿈이 든 셀을 CSV 규칙대로 감싼다.</summary>
    private static string Csv(string s)
    {
        s = s ?? "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return '"' + s.Replace("\"", "\"\"") + '"';
    }

    private static Verdict Judge(TMP_Text t, HashSet<TMP_Text> driven, HashSet<string> keys, out string reason)
    {
        reason = null;

        var loc = t.GetComponent<LocalizedText>();
        if (loc != null)
        {
            string key = KeyOf(loc);
            if (string.IsNullOrEmpty(key))
            {
                // 스크립트가 채우는 텍스트면 키를 주면 안 된다 — 서로 덮어쓰며 싸운다.
                // 붙은 컴포넌트를 떼는 게 답이므로 그렇게 안내한다
                reason = driven.Contains(t)
                    ? "키가 비어 있다 — 스크립트가 채우는 텍스트라 키를 주면 안 된다. 컴포넌트를 제거할 것"
                    : "키가 비어 있다";
                return Verdict.BadKey;
            }
            if (!keys.Contains(key))       { reason = $"'{key}' 가 localization.csv 에 없다"; return Verdict.BadKey; }
            return Verdict.Localized;
        }

        if (driven.Contains(t))                  return Verdict.ScriptDriven;
        if (string.IsNullOrWhiteSpace(t.text))   return Verdict.Empty;
        return Verdict.Missing;
    }

    /// <summary>
    /// 직렬화 필드로 TMP_Text를 참조하는 컴포넌트가 하나라도 있으면
    /// 그 텍스트는 스크립트가 채우는 것으로 본다.
    /// (SerializedObject로 훑기 때문에 필드 이름을 몰라도 되고, 배열·리스트 안쪽도 잡힌다)
    /// </summary>
    private static HashSet<TMP_Text> CollectScriptDrivenTexts(MonoBehaviour[] behaviours)
    {
        var result = new HashSet<TMP_Text>();

        foreach (var mb in behaviours)
        {
            if (mb == null || mb is LocalizedText) continue;   // LocalizedText 자신은 제외

            using var so = new SerializedObject(mb);
            var prop = so.GetIterator();

            // enterChildren: 첫 호출만 true — 이후 Next(false)로 형제만 훑으면 중첩을 놓친다
            bool enter = true;
            while (prop.NextVisible(enter))
            {
                enter = true;
                if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                if (prop.objectReferenceValue is TMP_Text tmp) result.Add(tmp);
            }
        }
        return result;
    }

    // ── CSV ───────────────────────────────────────────────────

    /// <summary>
    /// 키 목록만 필요하므로 각 줄의 첫 칸만 읽는다.
    /// (키에는 쉼표·따옴표가 없으므로 본격적인 CSV 파서가 필요 없다)
    /// </summary>
    private static HashSet<string> LoadCsvKeys()
    {
        var asset = Resources.Load<TextAsset>(CsvResourcePath);
        if (asset == null)
        {
            Debug.LogError($"[번역점검] Resources/{CsvResourcePath}.csv 를 찾을 수 없다. " +
                           "이 파일은 Assets 아래 Resources 폴더에 있어야 한다.");
            return null;
        }

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

    /// <summary>LocalizedText.key는 private이라 SerializedObject로 읽는다.</summary>
    private static string KeyOf(LocalizedText loc)
    {
        using var so = new SerializedObject(loc);
        return so.FindProperty("key")?.stringValue;
    }

    /// <summary>원문 그대로 — 줄바꿈·탭만 눈에 보이게 바꾼다 (공백은 건드리지 않는다).</summary>
    private static string Escaped(string s)
    {
        s = (s ?? "").Replace("\r", "").Replace("\n", "\\n").Replace("\t", "\\t");
        if (s.Length > 60) s = s.Substring(0, 60) + "…";
        return $"\"{s}\"";
    }

    private static string Preview(string s)
    {
        s = s.Replace('\n', ' ').Trim();
        return s.Length <= 40 ? s : s.Substring(0, 40) + "…";
    }

    private static string GetPath(Transform t)
    {
        string path = t.name;
        while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
        return path;
    }
}
