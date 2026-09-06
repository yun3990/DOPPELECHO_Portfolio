using System.Collections.Generic;
using Mirror;
using UnityEngine;

/// <summary>
/// 라운드 시작 게이트 — 접속한 전원이 로딩을 마칠 때까지 라운드를 붙잡아 둔다.
///
/// 흐름:
///   1. 클라이언트: 게임 씬 로드 완료 + 자기 플레이어 오브젝트 스폰 확인
///   2. 클라이언트: CmdReportReady()
///   3. 서버: 접속한 전원이 Ready → RoundManager.ServerBeginRound() → Playing
///   4. 모든 클라이언트: Phase가 Playing이 되면 로딩 화면을 닫는다
///
/// 서버 권위: 준비 여부와 시작 시점을 서버가 정한다. 클라이언트는 "나는 준비됐다"만 보고한다.
///
/// 무한 대기 방지 두 겹:
///   - 대기 대상을 매 프레임 NetworkServer.connections로 재평가 → 나간 사람은 자동 제외
///   - 그래도 안 끝나면 maxWaitSeconds 후 강제 시작
///
/// 배치: 게임 씬에 오브젝트 + 이 컴포넌트 + NetworkIdentity.
///       없으면 RoundManager가 Waiting에 머무르므로 반드시 배치해야 한다.
/// </summary>
[RequireComponent(typeof(NetworkIdentity))]
public class RoundReadyGate : NetworkBehaviour
{
    [Header("대기")]
    [Tooltip("전원이 준비되지 않아도 이 시간이 지나면 강제로 시작한다 (초)")]
    [SerializeField] private float maxWaitSeconds = 30f;

    [Tooltip("서버가 준비 상태를 다시 확인하는 주기 (초)")]
    [SerializeField] private float pollInterval = 0.25f;

    /// <summary>준비를 보고한 연결 id.</summary>
    private readonly HashSet<int> _ready = new();

    private double _deadline;
    private float  _nextPoll;
    private bool   _started;

    // 클라이언트: 준비 보고를 한 번만 보내기 위한 플래그
    private bool _reported;

    public override void OnStartServer()
    {
        _deadline = NetworkTime.time + Mathf.Max(1f, maxWaitSeconds);
        Debug.Log($"[ReadyGate] 플레이어 대기 시작 (최대 {maxWaitSeconds:F0}초)");
    }

    public override void OnStartClient()
    {
        // 아직 라운드가 시작 전이면 로딩 화면을 띄운다.
        // (이미 진행 중인 라운드에 늦게 들어온 경우에는 곧바로 닫힌다)
        if (RoundManager.Instance != null && RoundManager.Instance.Phase == RoundPhase.Waiting)
            LoadingScreen.Show(LoadingScreen.Mode.NetworkRound);
    }

    private void Update()
    {
        if (isServer) ServerTick();
        ClientTick();
    }

    // ── 서버 ──────────────────────────────────────────────────

    [Server]
    private void ServerTick()
    {
        if (_started) return;
        if (Time.time < _nextPoll) return;
        _nextPoll = Time.time + Mathf.Max(0.05f, pollInterval);

        var round = RoundManager.Instance;
        if (round == null || round.Phase != RoundPhase.Waiting) return;

        // 나간 사람은 대기 대상에서 자동으로 빠진다 (매번 현재 연결 기준으로 재평가)
        int total = 0;
        int ready = 0;

        foreach (var conn in NetworkServer.connections.Values)
        {
            if (conn == null || !conn.isReady) continue;   // 아직 씬 준비 전인 연결은 세지 않는다

            total++;
            if (_ready.Contains(conn.connectionId)) ready++;
        }

        bool everyoneReady = total > 0 && ready >= total;
        bool timedOut      = NetworkTime.time >= _deadline;

        if (!everyoneReady && !timedOut) return;

        _started = true;
        round.ServerBeginRound();

        Debug.Log(timedOut && !everyoneReady
            ? $"[ReadyGate] 대기 시간 초과 — 강제 시작 ({ready}/{total} 준비됨)"
            : $"[ReadyGate] 전원 준비 완료 ({ready}/{total}) — 라운드 시작");
    }

    /// <summary>클라이언트가 "내 쪽 준비 끝"이라고 알린다. 시점 판단은 서버가 한다.</summary>
    [Command(requiresAuthority = false)]
    private void CmdReportReady(NetworkConnectionToClient sender = null)
    {
        if (sender == null) return;

        if (_ready.Add(sender.connectionId))
            Debug.Log($"[ReadyGate] 준비 완료: conn {sender.connectionId} ({_ready.Count}명)");
    }

    // ── 클라이언트 ────────────────────────────────────────────

    private void ClientTick()
    {
        if (!NetworkClient.active) return;

        var round = RoundManager.Instance;
        if (round == null) return;

        if (round.Phase == RoundPhase.Waiting)
        {
            ReportWhenSpawned();
            return;
        }

        // 라운드가 시작되면 로딩 화면을 닫는다 (= 이 시점에 입력이 열린다)
        if (LoadingScreen.Instance != null && LoadingScreen.Instance.IsShowing)
            LoadingScreen.Hide();
    }

    /// <summary>
    /// 자기 플레이어 오브젝트가 스폰된 뒤에 준비를 보고한다.
    /// 씬 로드만으로는 아직 조작할 대상이 없기 때문이다.
    /// </summary>
    private void ReportWhenSpawned()
    {
        if (_reported) return;

        if (NetworkClient.localPlayer == null)
        {
            LoadingScreen.SetStep(LoadingScreen.Step.Spawn);
            return;
        }

        _reported = true;
        LoadingScreen.SetStep(LoadingScreen.Step.WaitingPlayers);
        CmdReportReady();
    }
}
