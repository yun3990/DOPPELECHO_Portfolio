using System.Collections;
using Mirror;
using UnityEngine;

/// <summary>전력 단계. 낮음 → 높음 순서라 크기 비교로도 판단할 수 있다.</summary>
public enum PowerLevel
{
    Blackout,   // 완전 암전 — 발전기에 연결된 모든 장치 정지
    Emergency,  // 비상전력 — 빨간 비상등 + 전력문만
    Main,       // 정상
}

/// <summary>
/// 맵 전력 총괄 (NetworkBehaviour) — 3단계 상태를 서버 권위로 관리한다.
///
///   발전기 OFF → Blackout(강제 12초) → Emergency → (발전기 복구) → Main
///
/// Blackout 동안에는 발전기도 켤 수 없다. 비상전력이 들어와야 복구를 시도할 수 있다.
///
/// ⚠ IsPowered의 의미는 "Main일 때만 true"로 유지한다.
///    카메라·터미널·검사기·패널 등 기존 소비자가 전부 이 프로퍼티를 보고 있어서,
///    그대로 두면 코드를 고치지 않아도 자동으로 Main 전용 장비가 된다.
///    비상전력에서도 쓸 수 있어야 하는 것(문)만 HasEmergencyPower를 본다.
///
/// ※ 이 컴포넌트가 붙은 씬 오브젝트에는 NetworkIdentity가 필요함.
/// </summary>
public class PowerSystem : NetworkBehaviour
{
    [Header("조명 — 일반 (Main에서만 점등)")]
    [SerializeField] private PoweredLight[] poweredLights;

    [Header("조명 — 비상등 (Emergency에서만 점등)")]
    [Tooltip("빨간 비상등. 일반 조명과 같은 PoweredLight를 쓴다")]
    [SerializeField] private PoweredLight[] emergencyLights;

    [Header("경고등 (Emergency에서 점멸)")]
    [Tooltip("Blackout은 진짜 암전이라 여기서도 꺼진다")]
    [SerializeField] private WarningLight[] warningLights;

    [Header("정전")]
    [Tooltip("발전기가 꺼진 뒤 비상전력이 들어오기까지의 완전 암전 시간 (초). " +
             "이 시간 동안에는 발전기도 켤 수 없다")]
    [SerializeField] private float blackoutDuration = 12f;
    [SerializeField] private BlackoutUI blackoutUI;

    [Header("사운드")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip powerOnClip;      // Main 복구
    [SerializeField] private AudioClip powerOffClip;     // Blackout 진입
    [SerializeField] private AudioClip emergencyOnClip;  // 비상전력 가동 (선택)

    [Header("발전기 연동")]
    [SerializeField] private GeneratorController generator;

    // ── 상태 (서버 권위) ──────────────────────────────────────

    [SyncVar(hook = nameof(OnSyncLevel))]
    private PowerLevel _level = PowerLevel.Main;

    public PowerLevel Level => _level;

    /// <summary>
    /// 전력 단계가 바뀔 때 발생 (모든 클라이언트에서). LED/Emission 오버라이드를 쓰는
    /// 오브젝트가 구독해 그때만 다시 그린다 — 매 프레임 폴링이 필요 없다.
    /// </summary>
    public event System.Action<PowerLevel> OnLevelChanged;

    /// <summary>정상 전력. 카메라·터미널·검사기 등 대부분의 장비가 이 값을 본다.</summary>
    public bool IsPowered => _level == PowerLevel.Main;

    /// <summary>비상전력 이상. 전력문처럼 최소 운영에 필요한 장치가 본다.</summary>
    public bool HasEmergencyPower => _level != PowerLevel.Blackout;

    /// <summary>완전 암전 중인가 (이 동안에는 발전기 시동도 불가).</summary>
    public bool InBlackout => _level == PowerLevel.Blackout;

    /// <summary>암전 남은 시간 (초). 표시용.</summary>
    public float BlackoutRemaining =>
        _level == PowerLevel.Blackout && _blackoutEndTime > 0d
            ? Mathf.Max(0f, (float)(_blackoutEndTime - NetworkTime.time))
            : 0f;

    [SyncVar] private double _blackoutEndTime;

    [Header("사보타지 보호")]
    [Tooltip("전력이 복구된 뒤 이 시간 동안 발전기 정지·과부하 사보타지를 모두 막는다 (초)")]
    [SerializeField] private float stabilizeSeconds = 60f;

    [SyncVar] private double _sabotageLockUntil;

    /// <summary>
    /// 복구 직후 안정화 중인가. 직접 발전기 OFF와 외부 단자함 과부하를 함께 막는 공통 잠금이다.
    /// </summary>
    public bool SabotageLocked => _sabotageLockUntil > 0d && NetworkTime.time < _sabotageLockUntil;

    /// <summary>안정화 남은 시간 (초). 표시용.</summary>
    public float StabilizeRemaining =>
        SabotageLocked ? Mathf.Max(0f, (float)(_sabotageLockUntil - NetworkTime.time)) : 0f;

    private Coroutine  _blackoutRoutine;
    private bool       _initialized;
    private PowerLevel _applied = (PowerLevel)(-1);

    // ── 라이프사이클 ──────────────────────────────────────────

    private void Start()
    {
        // ⚠ 무조건 정전으로 초기화하면 안 된다 — Start는 GeneratorController.OnStartServer보다
        //    뒤에 실행되므로 발전기가 막 켠 전력을 되돌린다. 현재 상태를 그대로 반영한다.
        ApplyLevel(_level, silent: true);
        _initialized = true;
    }

    public override void OnStartClient()
    {
        // 늦게 접속해도 현재 단계가 즉시 반영된다 (초기값이면 훅이 돌지 않으므로)
        if (!isServer) ApplyLevel(_level, silent: true);
    }

    // ── 서버 제어 ─────────────────────────────────────────────

    /// <summary>
    /// GeneratorController / ElectricBox에서 호출하는 기존 API.
    ///   on=true  → 즉시 Main 복구
    ///   on=false → Main이었다면 Blackout 시작 (이미 정전 계열이면 무시)
    /// </summary>
    public void SetPower(bool on)
    {
        // 순수 클라이언트는 SyncVar로 결과를 받으므로 무시
        if (NetworkClient.active && !NetworkServer.active) return;

        if (on)                          ServerSetLevel(PowerLevel.Main);
        else if (_level == PowerLevel.Main) ServerBeginBlackout();
    }

    private void ServerBeginBlackout()
    {
        _blackoutEndTime = NetworkTime.time + Mathf.Max(0f, blackoutDuration);
        ServerSetLevel(PowerLevel.Blackout);

        if (_blackoutRoutine != null) StopCoroutine(_blackoutRoutine);
        _blackoutRoutine = StartCoroutine(BlackoutRoutine());
    }

    private IEnumerator BlackoutRoutine()
    {
        yield return new WaitForSeconds(Mathf.Max(0f, blackoutDuration));
        _blackoutRoutine = null;

        // ⚠ 상태 재검증 — 도중에 복구됐다면 Emergency로 덮어쓰지 않는다.
        //    (StopCoroutine과 이 검사, 두 겹으로 막는다)
        if (_level == PowerLevel.Blackout) ServerSetLevel(PowerLevel.Emergency);
    }

    private void ServerSetLevel(PowerLevel next)
    {
        if (_level == next) return;

        // Main으로 복구되면 대기 중인 암전 타이머를 취소하고 안정화 보호를 건다
        if (next == PowerLevel.Main)
        {
            if (_blackoutRoutine != null) { StopCoroutine(_blackoutRoutine); _blackoutRoutine = null; }
            _blackoutEndTime = 0d;

            // 라운드 시작(_level 초기값이 이미 Main)에는 걸리지 않고, 실제 복구에만 적용된다
            _sabotageLockUntil = NetworkTime.time + Mathf.Max(0f, stabilizeSeconds);
        }

        _level = next;                    // SyncVar → 클라이언트 훅
        ApplyLevel(next, silent: false);  // 서버/호스트 즉시 반영

        // 암전에 들어가면 발전기도 멈춘다 (전기함 파괴처럼 외부 요인으로 꺼진 경우 대비).
        // StopGenerator → SetPower(false)로 되돌아오지만 이미 Main이 아니라 무시된다.
        if (next == PowerLevel.Blackout && generator != null) generator.StopGenerator();
    }

    // ── 표시 ──────────────────────────────────────────────────

    private void OnSyncLevel(PowerLevel _, PowerLevel next)
    {
        // 순수 클라이언트에서만 적용 (서버/호스트는 ServerSetLevel에서 이미 처리)
        if (!isServer) ApplyLevel(next, silent: !_initialized);
    }

    private void ApplyLevel(PowerLevel level, bool silent)
    {
        bool changed = _applied != level;
        _applied = level;

        bool main      = level == PowerLevel.Main;
        bool emergency = level == PowerLevel.Emergency;

        foreach (var l in poweredLights)    l?.SetPower(main);
        foreach (var l in emergencyLights)  l?.SetPower(emergency);
        foreach (var w in warningLights)    w?.SetWarning(emergency);

        blackoutUI?.SetBlackout(level == PowerLevel.Blackout);

        if (!silent && changed && audioSource != null)
        {
            AudioClip clip = level switch
            {
                PowerLevel.Main      => powerOnClip,
                PowerLevel.Blackout  => powerOffClip,
                PowerLevel.Emergency => emergencyOnClip,
                _                    => null,
            };
            if (clip != null) audioSource.PlayOneShot(clip);
        }

        if (changed) OnLevelChanged?.Invoke(level);

        Debug.Log($"[PowerSystem] {level}");
    }

    private void OnValidate() => blackoutDuration = Mathf.Max(0f, blackoutDuration);
}
