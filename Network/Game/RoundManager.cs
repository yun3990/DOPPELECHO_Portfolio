using System;
using System.Collections;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

/// <summary>
/// 게임 씬 라운드 진행/승패 관리 (싱글톤 NetworkBehaviour).
///
/// 타이머 방식: NetworkTime 기반 _roundEndTime(double) SyncVar 1개.
///   - Playing 시작 시: _roundEndTime = NetworkTime.time + mainRoundDuration(600s)
///   - FinalEscape 진입 시: _roundEndTime = NetworkTime.time + Min(remaining, finalEscapeMaxTime(180s))
///   - 클라이언트는 RemainingTime = Max(0, _roundEndTime - NetworkTime.time) 로 로컬 계산.
/// → 매 프레임 SyncVar 전송 없음. 씬 로딩 시차 영향 없음.
/// </summary>
public class RoundManager : NetworkBehaviour
{
    public static RoundManager Instance { get; private set; }

    [Header("라운드 타이머")]
    [SerializeField] private double mainRoundDuration  = 600.0; // 10분

    [Tooltip("RoundReadyGate가 라운드를 시작시키지 않을 때 강제로 시작하기까지의 시간 (초). 안전장치")]
    [SerializeField] private float noGateFallbackSeconds = 45f;
    [SerializeField] private double finalEscapeMaxTime = 180.0; // 최대 3분

    [Header("참조 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private ShuttleBoardingZone boardingZone;

    // ── 동기화 상태 ───────────────────────────────────────────

    [SyncVar(hook = nameof(OnSyncPhase))]
    private RoundPhase _phase = RoundPhase.Waiting;

    /// <summary>서버 기준 라운드(또는 FinalEscape) 종료 시각. 클라이언트가 로컬 계산에 사용.</summary>
    [SyncVar]
    private double _roundEndTime = 0;

    /// <summary>서버 기준 라운드(Playing) 시작 시각. 초반 흡수 잠금 등 기준.</summary>
    [SyncVar]
    private double _roundStartTime = 0;

    public double RoundStartTime => _roundStartTime;

    [SyncVar(hook = nameof(OnSyncRoundResult))]
    private RoundResult _roundResult = RoundResult.None;

    public RoundPhase  Phase      => _phase;

    /// <summary>
    /// 라운드가 진행 중인지 (플레이어 활동·흡수 등이 허용되는 구간).
    /// ShuttleReady는 '미션을 다 마쳤을 뿐 아직 진행 중'이고, FinalEscape도 마찬가지다 —
    /// Playing만 보면 미션 완료 순간 임포스터 기능이 통째로 꺼진다.
    /// </summary>
    public bool InProgress => _phase is RoundPhase.Playing
                                     or RoundPhase.ShuttleReady
                                     or RoundPhase.ShuttleDeparting
                                     or RoundPhase.FinalEscape;
    public RoundResult Result     => _roundResult;

    /// <summary>현재 남은 시간(초). 클라이언트가 NetworkTime으로 로컬 계산.</summary>
    public float RemainingTime => (float)Math.Max(0.0, _roundEndTime - NetworkTime.time);

    public event Action OnPhaseChanged;
    public event Action OnRoundFinished;

    // ── 서버 내부 상태 ────────────────────────────────────────

    private bool      _shuttleContaminated = false;
    private Coroutine _timerRoutine;

    // ── 라이프사이클 ──────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public override void OnStartServer()
    {
        if (boardingZone == null)
            boardingZone = FindObjectOfType<ShuttleBoardingZone>();

        // 방 설정의 라운드 시간 적용 (로비에서 호스트가 설정)
        mainRoundDuration = ActiveGameSettings.Current.roundTimeSeconds;

        // ⚠ 여기서 바로 시작하지 않는다.
        //    전원이 로딩을 마칠 때까지 Waiting에 머물고, RoundReadyGate가 ServerBeginRound를 부른다.
        //    (먼저 접속한 사람만 라운드 시간을 손해 보지 않도록 타이머도 그때 시작된다)
        ServerSetPhase(RoundPhase.Waiting);

        // 게이트가 씬에 없거나 응답하지 않으면 아무도 라운드를 시작시키지 않는다 — 안전장치
        StartCoroutine(NoGateFallback());
    }

    /// <summary>
    /// RoundReadyGate가 없는(또는 끝내 시작시키지 못한) 상황에서 라운드가 영영
    /// 시작되지 않는 것을 막는다. 정상 흐름에서는 게이트가 먼저 시작시킨다.
    /// </summary>
    private IEnumerator NoGateFallback()
    {
        yield return null;   // 씬 오브젝트가 모두 깨어날 기회를 준다

        // 게이트가 아예 없으면 기다릴 이유가 없다 — 즉시 시작한다.
        // (배치 전이라도 게임이 멈춘 것처럼 보이지 않도록)
        if (FindObjectOfType<RoundReadyGate>() == null)
        {
            Debug.LogWarning("[Round] 씬에 RoundReadyGate가 없어 로딩 대기 없이 바로 시작합니다. " +
                             "전원 로딩 대기를 쓰려면 게임 씬에 RoundReadyGate를 배치하세요.");
            ServerBeginRound();
            yield break;
        }

        yield return new WaitForSeconds(noGateFallbackSeconds);

        if (_phase != RoundPhase.Waiting) yield break;

        Debug.LogWarning("[Round] RoundReadyGate가 라운드를 시작시키지 못해 강제로 시작합니다.");
        ServerBeginRound();
    }

    /// <summary>
    /// 전원이 준비되면 RoundReadyGate가 호출한다 — 여기서부터 실제 라운드가 돈다.
    /// </summary>
    [Server]
    public void ServerBeginRound()
    {
        if (_phase != RoundPhase.Waiting) return;

        ServerSetPhase(RoundPhase.Playing);

        // 메인 타이머 시작
        _roundStartTime = NetworkTime.time;
        _roundEndTime   = NetworkTime.time + mainRoundDuration;
        StopTimer();
        _timerRoutine = StartCoroutine(RoundTimerCoroutine());

        Debug.Log($"[Round] 라운드 시작 ({mainRoundDuration:F0}초)");

        if (ShuttleMissionManager.Instance != null)
            ShuttleMissionManager.Instance.OnProgressChanged += ServerOnMissionProgress;

        // 요구 미션이 하나도 없는 방에서는 완료 이벤트가 영영 발생하지 않는다.
        // 구독 직후 한 번 판정해 곧바로 ShuttleReady로 올린다.
        // (미션 매니저가 먼저 초기화돼 이벤트를 이미 흘려보낸 경우도 여기서 복구된다)
        ServerOnMissionProgress();
    }

    public override void OnStopServer()
    {
        if (ShuttleMissionManager.Instance != null)
            ShuttleMissionManager.Instance.OnProgressChanged -= ServerOnMissionProgress;
        StopTimer();
    }

    // ── 타이머 코루틴 (메인·FinalEscape 공용) ─────────────────

    private IEnumerator RoundTimerCoroutine()
    {
        while (NetworkTime.time < _roundEndTime)
            yield return null;

        if (_phase != RoundPhase.Finished)
            ServerEndRound();
    }

    private void StopTimer()
    {
        if (_timerRoutine != null) StopCoroutine(_timerRoutine);
        _timerRoutine = null;
    }

    // ── 미션 → 셔틀 준비 ──────────────────────────────────────

    [Server]
    private void ServerOnMissionProgress()
    {
        if (_phase != RoundPhase.Playing) return;
        if (ShuttleMissionManager.Instance != null && ShuttleMissionManager.Instance.CanEscape())
            ServerSetPhase(RoundPhase.ShuttleReady);
    }

    // ── 셔틀 출발 ─────────────────────────────────────────────

    public bool CanBeginDeparture
    {
        get
        {
            if (_phase != RoundPhase.ShuttleReady) return false;
            if (ShuttleMissionManager.Instance != null && !ShuttleMissionManager.Instance.CanEscape()) return false;
            return true;
        }
    }

    public bool CanLaunchShuttle => CanBeginDeparture;
    public ShuttleBoardingZone BoardingZone => boardingZone;

    [Server]
    public bool ServerTryBeginDeparture()
    {
        // 요청 시점에 다시 판정 — 미션 완료 이벤트를 놓쳤어도 여기서 승격된다
        ServerOnMissionProgress();

        if (!CanBeginDeparture)
        {
            bool canEscape = ShuttleMissionManager.Instance == null
                          || ShuttleMissionManager.Instance.CanEscape();
            Debug.Log($"[Round] 출발 시작 불가 (phase={_phase}, canEscape={canEscape})");
            return false;
        }
        ServerSetPhase(RoundPhase.ShuttleDeparting);
        Debug.Log("[Round] 셔틀 출발 시퀀스 시작 → ShuttleDeparting");
        return true;
    }

    /// <summary>
    /// 확정 탑승자를 탈출 상태로 전환한다 (결과 판정은 하지 않는다).
    ///
    /// 출발 이동 연출보다 **먼저** 불러야 한다. 탑승자는 셔틀에 부모로 붙어 있지 않아서,
    /// 셔틀이 먼저 움직이면 발밑 바닥이 빠져나가 공중에 떴다가 바닥에 착지해 버린다.
    /// </summary>
    [Server]
    public void ServerMarkShuttleBoarders(List<NetworkPlayerState> boarders)
    {
        bool humanAboard    = false;
        bool imposterAboard = false;
        int  count          = 0;

        if (boarders != null)
        {
            foreach (var ps in boarders)
            {
                if (ps == null || ps.LifeState != PlayerLifeState.Alive) continue;
                if (ps.ServerIsImposter) imposterAboard = true;
                else                     humanAboard    = true;
                ps.ServerSetLifeState(PlayerLifeState.EscapedByShuttle);
                count++;
            }
        }

        _shuttleContaminated = humanAboard && imposterAboard;
        Debug.Log($"[Round] 셔틀 탈출 확정: 탑승 {count}명, 인간={humanAboard}, 임포스터={imposterAboard}, 오염={_shuttleContaminated}");
    }

    /// <summary>출발 연출이 끝난 뒤의 결과 판정 (최종 탈출 단계 진입 또는 라운드 종료).</summary>
    [Server]
    public void ServerFinishShuttleDeparture() => ServerHandlePostDeparture();

    /// <summary>탑승자 전환 + 결과 판정을 한 번에. 연출을 나누지 않는 경로용.</summary>
    [Server]
    public void ServerCompleteShuttleEscape(List<NetworkPlayerState> boarders)
    {
        ServerMarkShuttleBoarders(boarders);
        ServerHandlePostDeparture();
    }

    [Server]
    public void ServerLaunchShuttle()
    {
        if (!ServerTryBeginDeparture()) return;

        List<NetworkPlayerState> boarders = (boardingZone != null && boardingZone.HasAnyAliveBoarder())
            ? boardingZone.GetValidBoarders()
            : ResolveInteractorBoarders();

        ServerCompleteShuttleEscape(boarders);
    }

    [Server]
    public List<NetworkPlayerState> ResolveInteractorBoarders()
    {
        var list = new List<NetworkPlayerState>();
        var interactor = NetworkInteraction.CurrentInteractor;
        var ps = interactor != null
            ? (interactor.GetComponent<NetworkPlayerState>()
               ?? interactor.GetComponentInChildren<NetworkPlayerState>(true)
               ?? interactor.GetComponentInParent<NetworkPlayerState>())
            : null;
        if (ps != null && ps.LifeState == PlayerLifeState.Alive) list.Add(ps);
        return list;
    }

    [Server]
    private void ServerHandlePostDeparture()
    {
        int  aliveHumans  = CountAliveHumansOnStation();
        bool podAvailable = EscapePodManager.Instance?.HasUsablePod ?? false;

        if (aliveHumans >= 1 && podAvailable)
            ServerBeginFinalEscape();
        else
            ServerEndRound();
    }

    // ── FinalEscape — min(남은 시간, 180초) ───────────────────

    [Server]
    private void ServerBeginFinalEscape()
    {
        if (_phase == RoundPhase.FinalEscape) return; // 중복 방지

        double remaining  = _roundEndTime - NetworkTime.time;
        double finalTime  = Math.Min(remaining, finalEscapeMaxTime);
        _roundEndTime     = NetworkTime.time + finalTime;

        StopTimer();
        _timerRoutine = StartCoroutine(RoundTimerCoroutine());

        ServerSetPhase(RoundPhase.FinalEscape);
        Debug.Log($"[Round] 최종 탈출 시작 ({finalTime:F0}초, 기존 잔여={remaining:F0}초)");
    }

    // ── 라운드 재검사 ─────────────────────────────────────────

    [Server]
    public void ServerEvaluateRound()
    {
        if (_phase == RoundPhase.Finished)         return;
        if (_phase == RoundPhase.ShuttleDeparting) return;
        if (_ending)                               return;

        if (CountAliveHumansOnStation() == 0)
            ServerEndRound();
    }

    // ── 결과 일괄 계산 & 종료 ─────────────────────────────────

    private bool _ending = false;

    [Server]
    private void ServerEndRound()
    {
        if (_ending || _phase == RoundPhase.Finished) return;
        _ending = true;

        StopTimer();
        ServerMarkRemainingLeftBehind();
        ServerResolveAndFinish();
    }

    [Server]
    private void ServerMarkRemainingLeftBehind()
    {
        foreach (var ps in NetworkPlayerState.All)
            if (ps != null && ps.LifeState == PlayerLifeState.Alive)
                ps.ServerSetLifeState(PlayerLifeState.LeftBehind);
    }

    [Server]
    private void ServerResolveAndFinish()
    {
        if (_phase == RoundPhase.Finished) return;
        StopTimer();

        int humansWon = 0;
        var snapshot = new List<NetworkPlayerState>(NetworkPlayerState.All);

        foreach (var ps in snapshot)
        {
            if (ps == null || ps.ServerIsImposter) continue;
            PlayerResult r = ResolveHumanResult(ps.LifeState);
            ps.ServerSetResult(r);
            if (r == PlayerResult.HumanVictory) humansWon++;
        }

        bool anyImposterWon = false;
        foreach (var ps in snapshot)
        {
            if (ps == null || !ps.ServerIsImposter) continue;
            PlayerResult r = ResolveImposterResult(ps.LifeState, humansWon);
            ps.ServerSetResult(r);
            if (r == PlayerResult.ImposterVictory) anyImposterWon = true;
        }

        foreach (var ps in snapshot)
            if (ps != null) ps.ServerRevealRole();

        bool humansSucceeded = humansWon > 0;
        _roundResult = (humansSucceeded && anyImposterWon) ? RoundResult.Mixed
                     : humansSucceeded                     ? RoundResult.HumansSucceeded
                     : RoundResult.ImpostersSucceeded;

        ServerSetPhase(RoundPhase.Finished);
        Debug.Log($"[Round] 종료 — 결과={_roundResult} (인간승리 {humansWon}명, 임포스터승리 {anyImposterWon})");
    }

    private PlayerResult ResolveHumanResult(PlayerLifeState life) => life switch
    {
        PlayerLifeState.EscapedByShuttle => _shuttleContaminated ? PlayerResult.SurvivedButLost : PlayerResult.HumanVictory,
        PlayerLifeState.EscapedByPod     => PlayerResult.HumanVictory,
        PlayerLifeState.Dead             => PlayerResult.Dead,
        PlayerLifeState.LeftBehind       => PlayerResult.LeftBehind,
        PlayerLifeState.Disconnected     => PlayerResult.Disconnected,
        _                                => PlayerResult.LeftBehind,
    };

    private PlayerResult ResolveImposterResult(PlayerLifeState life, int humansWon)
    {
        bool win = (life == PlayerLifeState.EscapedByShuttle && _shuttleContaminated) || humansWon == 0;
        if (win) return PlayerResult.ImposterVictory;

        return life switch
        {
            PlayerLifeState.EscapedByShuttle => PlayerResult.SurvivedButLost,
            PlayerLifeState.EscapedByPod     => PlayerResult.SurvivedButLost,
            PlayerLifeState.Dead             => PlayerResult.Dead,
            PlayerLifeState.LeftBehind       => PlayerResult.LeftBehind,
            PlayerLifeState.Disconnected     => PlayerResult.Disconnected,
            _                                => PlayerResult.LeftBehind,
        };
    }

    // ── 집계 ─────────────────────────────────────────────────

    public int CountAliveHumansOnStation()    => CountOnStation(human: true,  alive: true);
    public int CountAliveImpostersOnStation() => CountOnStation(human: false, alive: true);

    public int CountByLifeState(PlayerLifeState state, bool humansOnly)
    {
        int n = 0;
        foreach (var ps in NetworkPlayerState.All)
        {
            if (ps == null) continue;
            if (humansOnly && ps.ServerIsImposter) continue;
            if (ps.LifeState == state) n++;
        }
        return n;
    }

    private int CountOnStation(bool human, bool alive)
    {
        int n = 0;
        foreach (var ps in NetworkPlayerState.All)
        {
            if (ps == null) continue;
            if (ps.ServerIsImposter == human) continue;
            if (alive && ps.LifeState != PlayerLifeState.Alive) continue;
            n++;
        }
        return n;
    }

    // ── 단계 변경 ─────────────────────────────────────────────

    [Server]
    public void ServerSetPhase(RoundPhase phase)
    {
        if (_phase == phase) return;
        _phase = phase;
    }

    // ── SyncVar 훅 ────────────────────────────────────────────

    private void OnSyncPhase(RoundPhase _, RoundPhase next)
    {
        OnPhaseChanged?.Invoke();
        if (next == RoundPhase.Finished) OnRoundFinished?.Invoke();
    }

    private void OnSyncRoundResult(RoundResult _, RoundResult __) { }
}
