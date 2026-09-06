using System.Collections;
using Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Project.Settings;

/// <summary>
/// 범용 로딩 화면 — 씬과 네트워크가 실제로 준비될 때까지 게임 화면을 가린다.
///
/// 씬을 넘어 유지되는 싱글톤(DontDestroyOnLoad)이라 메인 메뉴 진입, 씬 전환,
/// 로비 → 게임 시작에서 같은 화면을 재사용한다.
///
/// 완료 조건은 모드로 나뉜다:
///   Scene        — AsyncOperation 로드 완료 = 끝 (메뉴 등 일반 씬)
///   NetworkRound — 씬 로드 + 플레이어 스폰 + 전원 Ready = 끝 (게임 씬)
///                  Ready 판정은 RoundReadyGate(서버 권위)가 한다.
///
/// ⚠ 화면이 열려 있는 동안 LocalInputLock을 잡는다.
///    그래서 "화면이 사라지는 시점 = 입력이 열리는 시점"이 구조적으로 일치하고,
///    먼저 로딩이 끝난 플레이어가 돌아다니는 일이 없다.
///
/// 배치: 부트 씬(또는 메인 메뉴)에 이 컴포넌트를 가진 Canvas를 하나 두면 된다.
///       root는 비활성으로 저장할 것.
/// </summary>
public class LoadingScreen : MonoBehaviour
{
    public enum Mode
    {
        /// <summary>씬 로드만 끝나면 닫는다 (메뉴 등).</summary>
        Scene,
        /// <summary>씬 로드 + 스폰 + 전원 Ready까지 기다린다 (게임 씬).</summary>
        NetworkRound,
    }

    /// <summary>현재 표시 중인 단계 — 문구·진행률 표시에 쓰인다.</summary>
    public enum Step
    {
        SceneLoad,      // 씬 로드 중
        Spawn,          // 플레이어 오브젝트 대기
        Sync,           // 초기 동기화
        WaitingPlayers, // 다른 플레이어 대기 (불확정)
        Done,
    }

    public static LoadingScreen Instance { get; private set; }

    [Header("표시")]
    [Tooltip("로딩 화면 전체. 비활성으로 저장할 것")]
    [SerializeField] private GameObject root;
    [SerializeField] private CanvasGroup canvasGroup;

    [Header("배경 (여러 장이면 무작위)")]
    [SerializeField] private Image     backgroundImage;
    [SerializeField] private Sprite[]  backgrounds;

    [Header("진행률")]
    [SerializeField] private Image progressFill;
    [Tooltip("진행률을 알 수 없는 단계에서 바가 맥동하는 속도")]
    [SerializeField] private float indeterminateSpeed = 0.8f;
    [Tooltip("맥동할 때 가장 흐려지는 정도")]
    [Range(0f, 1f)][SerializeField] private float indeterminateMinAlpha = 0.35f;

    [Header("문구")]
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text tipTitleText;
    [SerializeField] private TMP_Text tipBodyText;

    [Header("TIP / NOTICE")]
    [SerializeField] private LoadingTipTable tipTable;

    [Header("Localization 키")]
    [SerializeField] private string keySceneLoad      = "loading.status.scene";
    [SerializeField] private string keySpawn          = "loading.status.spawn";
    [SerializeField] private string keySync           = "loading.status.sync";
    [SerializeField] private string keyWaitingPlayers = "loading.status.waiting";

    [Header("페이드")]
    [SerializeField] private float fadeSeconds = 0.25f;

    [Header("최소 표시 시간 (초)")]
    [Tooltip("너무 빨리 지나가 깜빡이는 것처럼 보이지 않도록")]
    [SerializeField] private float minimumShowSeconds = 0.6f;

    [Header("안전장치")]
    [Tooltip("이 시간이 지나면 무슨 일이 있어도 화면을 닫는다. " +
             "완료 신호를 놓쳐 화면에 갇히는 것을 막는 최후 수단 (0 = 사용 안 함)")]
    [SerializeField] private float maxShowSeconds = 25f;

    // ── 상태 ──────────────────────────────────────────────────

    public bool IsShowing { get; private set; }

    /// <summary>현재 완료 조건 모드 (누가 Hide를 책임지는지 구분용).</summary>
    public Mode CurrentMode => _mode;

    private Mode   _mode;
    private Step   _step;
    private float  _progress;          // 0~1 (확정 구간에서만 의미)
    private bool   _indeterminate;
    private float  _shownAt;
    private bool   _lockAcquired;
    private bool   _closing;        // 닫는 중 (중복 호출 방지)
    private Coroutine _fadeRoutine;

    private LoadingTipTable.Entry _tip;

    // 맥동에 쓰기 위해 원래 알파를 기억해둔다 (확정 구간에서 그대로 복구)
    private float _fillBaseAlpha = 1f;

    private void SetFillAlpha(float alpha)
    {
        if (progressFill == null) return;

        Color c = progressFill.color;
        if (Mathf.Approximately(c.a, alpha)) return;

        c.a = alpha;
        progressFill.color = c;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        transform.SetParent(null);
        DontDestroyOnLoad(gameObject);

        if (root != null && root.activeSelf) root.SetActive(false);
        IsShowing = false;

        if (progressFill != null) _fillBaseAlpha = progressFill.color.a;
    }

    private void OnEnable()  => LocalizationManager.OnLanguageChanged += RefreshTexts;
    private void OnDisable() => LocalizationManager.OnLanguageChanged -= RefreshTexts;

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        ReleaseLock();
    }

    // ── 표시 / 종료 ───────────────────────────────────────────

    /// <summary>로딩 화면을 연다. 이미 열려 있으면 모드만 갱신한다.</summary>
    public static void Show(Mode mode)
    {
        if (Instance == null)
        {
            Debug.LogWarning("[Loading] LoadingScreen이 씬에 없습니다 — 로딩 화면 없이 진행합니다.");
            return;
        }
        Instance.ShowInternal(mode);
    }

    public static void SetStep(Step step, float progress = -1f)
        => Instance?.SetStepInternal(step, progress);

    public static void Hide() => Instance?.HideInternal();

    /// <summary>
    /// 일반 씬을 비동기로 로드하며 로딩 화면을 띄운다 (메뉴 등).
    /// 네트워크 씬 전환에는 쓰지 않는다 — 그건 Mirror가 직접 씬을 바꾼다.
    /// </summary>
    public static void LoadScene(string sceneName)
    {
        if (Instance == null)
        {
            SceneManager.LoadScene(sceneName);
            return;
        }
        Instance.StartCoroutine(Instance.LoadSceneRoutine(sceneName));
    }

    private void ShowInternal(Mode mode)
    {
        _mode = mode;

        if (!IsShowing)
        {
            IsShowing     = true;
            _closing      = false;
            _shownAt      = Time.unscaledTime;

            PickBackground();
            PickTip();
            AcquireLock();

            if (root != null) root.SetActive(true);
            StartFade(1f);
        }

        SetStepInternal(Step.SceneLoad, 0f);
    }

    private void SetStepInternal(Step step, float progress)
    {
        _step = step;

        // 진행률을 알 수 없는 구간에서만 맥동시킨다.
        // Sync는 씬 로드가 끝난 직후의 짧은 마무리 단계라 확정으로 둔다.
        _indeterminate = step is Step.Spawn or Step.WaitingPlayers;

        if (progress >= 0f) _progress = Mathf.Clamp01(progress);

        RefreshTexts();
    }

    private void HideInternal()
    {
        if (!IsShowing || _closing) return;

        _closing = true;
        StartCoroutine(HideRoutine());
    }

    private IEnumerator HideRoutine()
    {
        // 너무 빨리 사라져 깜빡이는 것처럼 보이지 않게
        float elapsed = Time.unscaledTime - _shownAt;
        if (elapsed < minimumShowSeconds)
            yield return new WaitForSecondsRealtime(minimumShowSeconds - elapsed);

        _step = Step.Done;
        StartFade(0f);

        if (fadeSeconds > 0f) yield return new WaitForSecondsRealtime(fadeSeconds);

        IsShowing = false;
        _closing  = false;
        if (root != null) root.SetActive(false);

        // 화면이 완전히 사라지는 순간에 입력을 연다
        ReleaseLock();
    }

    // ── 일반 씬 로드 ──────────────────────────────────────────

    private IEnumerator LoadSceneRoutine(string sceneName)
    {
        ShowInternal(Mode.Scene);

        AsyncOperation op = SceneManager.LoadSceneAsync(sceneName);
        if (op == null)
        {
            Debug.LogError($"[Loading] 씬 '{sceneName}'을 찾을 수 없습니다.");
            HideInternal();
            yield break;
        }

        op.allowSceneActivation = true;

        while (!op.isDone)
        {
            // AsyncOperation.progress는 0.9에서 멈추고 나머지가 활성화 단계다
            SetStepInternal(Step.SceneLoad, Mathf.Clamp01(op.progress / 0.9f));
            yield return null;
        }

        SetStepInternal(Step.Sync, 1f);
        yield return null;   // 새 씬의 Awake/Start가 한 번 돌 기회를 준다

        HideInternal();
    }

    // ── 표시 갱신 ─────────────────────────────────────────────

    private void Update()
    {
        if (!IsShowing) return;

        TrackNetworkSceneLoad();
        CheckSelfClose();

        if (progressFill == null) return;

        // ⚠ RectTransform은 건드리지 않는다 — 인스펙터에서 잡아둔 레이아웃을 그대로 둔다.
        //    표시는 fillAmount로만 한다 (Image Type = Filled 필요).
        if (_indeterminate)
        {
            // 진행률을 알 수 없는 구간 — 바는 가득 채운 채 밝기만 맥동시킨다.
            // 바를 왕복시키면 진행이 되돌아가는 것처럼 보여 어색하다.
            progressFill.fillAmount = 1f;

            float pulse = Mathf.PingPong(Time.unscaledTime * indeterminateSpeed, 1f);
            SetFillAlpha(Mathf.Lerp(indeterminateMinAlpha, 1f, pulse) * _fillBaseAlpha);
            return;
        }

        SetFillAlpha(_fillBaseAlpha);

        // 씬이 순식간에 로드되면 실제 진행률이 몇 프레임 만에 끝나 바가 움직일 틈이 없다.
        // 최소 표시 시간 동안은 시간 비례로도 차오르게 해서 진행이 눈에 보이게 한다.
        float target = _progress;
        if (minimumShowSeconds > 0f)
        {
            float byTime = (Time.unscaledTime - _shownAt) / minimumShowSeconds;
            target = Mathf.Max(target, Mathf.Clamp01(byTime));
        }

        progressFill.fillAmount = Mathf.MoveTowards(progressFill.fillAmount, target,
                                                    Time.unscaledDeltaTime * 4f);
    }

    /// <summary>
    /// Mirror가 씬을 바꿀 때는 LoadSceneRoutine을 타지 않으므로 진행률을 갱신할 사람이 없다.
    /// NetworkManager가 노출하는 AsyncOperation을 직접 읽어 반영한다
    /// (로비 진입·게임 씬 전환 모두 이 경로다).
    /// </summary>
    private void TrackNetworkSceneLoad()
    {
        AsyncOperation op = NetworkManager.loadingSceneAsync;

        if (_step != Step.SceneLoad) return;   // 스폰·대기 단계로 넘어갔으면 건드리지 않는다
        if (op == null) return;

        // progress는 0.9에서 멈추고 나머지가 활성화 단계다
        _progress = Mathf.Clamp01(op.progress / 0.9f);
    }

    /// <summary>
    /// 완료 신호를 놓쳐 화면에 갇히는 것을 막는다.
    ///
    /// Scene 모드는 씬 로드가 끝나면 스스로 닫는다 — 호출부가 Hide를 부르지 못해도
    /// (호스트/클라이언트에 따라 Mirror 콜백 경로가 다르다) 화면이 남지 않는다.
    /// 어떤 모드든 maxShowSeconds가 지나면 강제로 닫는다.
    /// </summary>
    private void CheckSelfClose()
    {
        if (_closing) return;

        float shown = Time.unscaledTime - _shownAt;

        // Scene 모드: 씬 로드가 진행 중이 아니면 닫는다.
        // ⚠ "로드를 관측한 적이 있는지"로 판단하면 안 된다 —
        //    호스트는 화면이 뜨기 전에 로드가 이미 끝나 있어 그 조건이 영영 성립하지 않고,
        //    결국 타임아웃까지 기다리게 된다(클라이언트만 빨리 끝나 보이는 원인).
        //    최소 표시 시간이 지난 뒤에 확인하므로 로드 시작 전에 닫힐 일도 없다.
        if (_mode == Mode.Scene && shown >= minimumShowSeconds
            && NetworkManager.loadingSceneAsync == null)
        {
            HideInternal();
            return;
        }

        if (maxShowSeconds > 0f && shown >= maxShowSeconds)
        {
            Debug.LogWarning($"[Loading] {maxShowSeconds:F0}초가 지나 로딩 화면을 강제로 닫습니다 " +
                             $"(mode={_mode}, step={_step}). 완료 신호가 오지 않았습니다.");
            HideInternal();
        }
    }

    private void RefreshTexts()
    {
        if (statusText != null)
        {
            string key = _step switch
            {
                Step.SceneLoad      => keySceneLoad,
                Step.Spawn          => keySpawn,
                Step.Sync           => keySync,
                Step.WaitingPlayers => keyWaitingPlayers,
                _                   => keySceneLoad,
            };
            statusText.text = LocalizationManager.Get(key);
        }

        if (_tip == null) return;

        if (tipTitleText != null) tipTitleText.text = LocalizationManager.Get(_tip.titleKey);
        if (tipBodyText  != null) tipBodyText.text  = LocalizationManager.Get(_tip.bodyKey);
    }

    private void PickTip()
    {
        _tip = tipTable != null ? tipTable.PickRandom() : null;

        bool show = _tip != null;
        if (tipTitleText != null) tipTitleText.gameObject.SetActive(show);
        if (tipBodyText  != null) tipBodyText.gameObject.SetActive(show);
    }

    private void PickBackground()
    {
        if (backgroundImage == null || backgrounds == null || backgrounds.Length == 0) return;

        Sprite pick = backgrounds[UnityEngine.Random.Range(0, backgrounds.Length)];
        if (pick != null) backgroundImage.sprite = pick;
    }

    // ── 페이드 ────────────────────────────────────────────────

    private void StartFade(float target)
    {
        if (canvasGroup == null) return;

        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);

        if (fadeSeconds <= 0f) { canvasGroup.alpha = target; return; }

        _fadeRoutine = StartCoroutine(FadeRoutine(target));
    }

    private IEnumerator FadeRoutine(float target)
    {
        float from = canvasGroup.alpha;
        float t = 0f;

        while (t < fadeSeconds)
        {
            t += Time.unscaledDeltaTime;
            canvasGroup.alpha = Mathf.Lerp(from, target, t / fadeSeconds);
            yield return null;
        }

        canvasGroup.alpha = target;
        _fadeRoutine = null;
    }

    // ── 입력 잠금 ─────────────────────────────────────────────

    private void AcquireLock()
    {
        if (_lockAcquired) return;
        _lockAcquired = true;
        LocalInputLock.Acquire();
    }

    private void ReleaseLock()
    {
        if (!_lockAcquired) return;
        _lockAcquired = false;
        LocalInputLock.Release();
    }
}
