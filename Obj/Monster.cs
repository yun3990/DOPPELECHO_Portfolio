using System.Collections;
using Mirror;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 괴물 AI — Mirror NetworkBehaviour 기반 상태 머신 (서버 권위).
/// Contained → (격리문 열림) → Idle → Chase → Attack
///                                        ↑←←←←↙
/// 어느 상태에서든 스턴건/봉 맞으면 → Stunned → 복귀, HP 0 → Dead.
///
/// 소리 추적: Idle(=순찰) 중 들리는 범위의 NoiseEvent가 발생하면 Investigate로 전환해
/// 그 위치로 이동한다. 이동 중 새 소리가 들리면 목표를 갱신하고, 도착 후 소리가 끊기면
/// searchDuration 동안 주변을 살핀 뒤 Idle로 돌아간다.
/// 도착 지점의 소음원이 아직 울리고 있고 IHittable이면 기존 공격 경로로 부순다.
/// 플레이어 추적(Chase)이 소리 조사보다 항상 우선한다.
///
/// 소리를 내는 쪽이 무엇인지는 알지 못한다 — INoiseSource / IHittable 인터페이스로만 판단하므로
/// 나중에 발소리·총성·문 소리를 붙여도 이 파일은 손대지 않아도 된다.
///
/// 서버: NavMeshAgent + 상태 전이 + HP + 데미지 판정.
/// 클라이언트: _state/_moveSpeed SyncVar로 애니메이션만 구동(이동은 NetworkTransform).
/// 오프라인(싱글): Mirror 비활성 시 로컬에서 직접 동작.
///
/// 인스펙터 필수: NetworkIdentity, NetworkTransform(Server→Client), Collider(attackLayer).
/// Animator 파라미터: Speed(Float), Attack(Trigger), IsStunned(Bool), IsDead(Bool).
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class Monster : NetworkBehaviour, IHittable
{
    // ⚠ 새 상태는 반드시 뒤에 추가한다 — 기존 씬/프리팹의 직렬화 값이 밀리지 않도록.
    public enum State { Contained, Idle, Chase, Attack, Stunned, Dead, Investigate }

    [Header("감지")]
    [Tooltip("시야 감지 거리. 0으로 두면 소리에만 반응하는 청각 전용 몬스터가 된다")]
    [SerializeField] private float detectRange = 10f;
    [SerializeField] private float attackRange     = 1.8f;  // Attack 진입 거리
    [SerializeField] private float attackExitRange = 2.3f;  // Attack 이탈 거리 (히스테리시스)
    [SerializeField] private float loseRange   = 18f;

    [Header("이동 속도")]
    [SerializeField] private float patrolSpeed = 1.5f;
    [SerializeField] private float chaseSpeed  = 4f;

    [Header("공격")]
    [SerializeField] private float attackDamage   = 50f;
    [SerializeField] private float attackCooldown = 2f;
    [Tooltip("켜면 Attack 애니메이션의 OnAttackHitFrame 이벤트에서 피해 적용. 끄면 공격 시작과 동시에 즉시 피해.")]
    [SerializeField] private bool  damageOnAnimationEvent = false;

    [Header("체력")]
    [SerializeField] private float maxHp = 200f;

    [Header("스턴")]
    [SerializeField] private float stunDuration = 3f;

    [Header("소리 감지")]
    [Tooltip("소리의 Detection Radius에 곱하는 청력. 1 = 설정된 반경 그대로, 2 = 두 배로 잘 들림")]
    [SerializeField] private float hearingMultiplier = 1f;
    [Tooltip("아무리 큰 소리여도 이 거리를 넘으면 듣지 못한다")]
    [SerializeField] private float maxHearingRange = 40f;

    [Header("소리 우선순위")]
    [Tooltip("현재 쫓는 소리에 대한 관심이 식는 속도 (초당). " +
             "클수록 작은 소리에도 금방 다시 반응하고, 작을수록 큰 소리를 끝까지 쫓는다")]
    [SerializeField] private float interestDecayPerSecond = 1.5f;
    [Tooltip("들리는 한계 거리에서 난 소리에 곱해지는 가중치. " +
             "1이면 거리를 무시하고 세기만 보고, 낮을수록 가까운 소리가 유리하다")]
    [Range(0f, 1f)][SerializeField] private float farNoiseWeight = 0.3f;

    [Header("소리 조사")]
    [Tooltip("소리가 난 지점에 '도착했다'고 볼 거리")]
    [SerializeField] private float investigateArriveDistance = 1.5f;
    [Tooltip("도착 후 새 소리가 없을 때 주변을 살피는 시간 (초). 끝나면 순찰 복귀")]
    [SerializeField] private float searchDuration = 3.5f;

    [Header("소음원 공격")]
    [Tooltip("아직 울리는 소음원을 때리기 시작하는 거리")]
    [SerializeField] private float noiseAttackRange = 2f;

    [Header("순찰 포인트 (Idle)")]
    [SerializeField] private Transform[] patrolPoints;
    [SerializeField] private float       patrolWaitTime = 2f;

    [Header("애니메이터 (선택)")]
    [SerializeField] private Animator animator;

    // ── 동기화 상태 ──────────────────────────────────────────

    [SyncVar] private State _state = State.Contained;
    [SyncVar] private float _moveSpeed;   // 서버 NavMeshAgent 실제 속도 → Blend Tree

    public State CurrentState => _state;

    // ── 서버 전용 내부 상태 ──────────────────────────────────

    private NavMeshAgent _agent;
    private PlayerHealth _target;
    private Collider[]   _colliders;
    private float        _currentHp;
    private float        _attackTimer;
    private float        _detectionTimer;   // FindPlayer 호출 간격 제한
    private int          _patrolIndex;
    private bool         _patrolWaiting;
    private State        _stateBeforeStun;

    // 소리 추적 (서버 전용)
    private Vector3      _noisePosition;    // 마지막으로 들은 소리 위치
    private bool         _hasNoise;
    private float        _noiseScore;       // 현재 쫓는 소리의 관심도 (시간이 지나면 식는다)
    private float        _searchTimer;      // 도착 후 남은 조사 시간
    private INoiseSource _noiseSource;      // 아직 울리는 소음원 (있을 때만)
    private IHittable    _noiseHittable;    // 그 소음원을 때릴 수 있으면 여기로 (기존 Damage 경로)

    private State _shownState = (State)(-1); // 클라 애니메이션 엣지 감지

    /// <summary>서버 또는 완전 오프라인일 때만 AI/데미지 권위 보유</summary>
    private bool HasAuthority => isServer || (!NetworkClient.active && !NetworkServer.active);

    // ── 초기화 ───────────────────────────────────────────────

    private void Awake()
    {
        _agent         = GetComponent<NavMeshAgent>();
        _agent.enabled = false;                       // Release 전까지 정지 (클라는 계속 비활성)
        _colliders     = GetComponentsInChildren<Collider>(true);
        _currentHp     = maxHp;
    }

    private void OnEnable()  => NoiseSystem.OnNoise += OnNoiseHeard;
    private void OnDisable() => NoiseSystem.OnNoise -= OnNoiseHeard;

    private void Update()
    {
        if (HasAuthority) ServerTick();
        ApplyVisualState();   // 호스트·클라 공통 (애니메이션은 _state/_moveSpeed 기준)
    }

    // ── 서버 AI 틱 ───────────────────────────────────────────

    private void ServerTick()
    {
        _attackTimer    = Mathf.Max(0f, _attackTimer    - Time.deltaTime);
        _detectionTimer = Mathf.Max(0f, _detectionTimer - Time.deltaTime);

        switch (_state)
        {
            case State.Idle:        UpdateIdle();        break;
            case State.Investigate: UpdateInvestigate(); break;
            case State.Chase:       UpdateChase();       break;
            case State.Attack:      UpdateAttack();      break;
        }

        // 실제 이동 속도를 SyncVar에 (양자화로 동기화 트래픽 절감)
        _moveSpeed = (_agent.enabled && !_agent.isStopped)
            ? Mathf.Round(_agent.velocity.magnitude * 10f) / 10f
            : 0f;
    }

    // ── 외부 호출: 격리 해제 (격리문에서 서버 측 호출) ───────

    public void Release()
    {
        if (!HasAuthority) return;
        if (_state != State.Contained) return;
        _agent.enabled = true;
        SetState(State.Idle);
        Debug.Log($"[Monster] {name} 격리 해제");
    }

    // ── Idle / 순찰 ───────────────────────────────────────────

    private void UpdateIdle()
    {
        if (_detectionTimer <= 0f)
        {
            _detectionTimer = 0.4f;
            PlayerHealth player = FindNearestPlayer(detectRange);
            if (player != null) { _target = player; SetState(State.Chase); return; }
        }

        if (patrolPoints.Length == 0) return;
        if (!_agent.pathPending && _agent.remainingDistance < 0.5f && !_patrolWaiting)
            StartCoroutine(PatrolWait());
    }

    private IEnumerator PatrolWait()
    {
        _patrolWaiting = true;
        yield return new WaitForSeconds(patrolWaitTime);

        // 대기 중 Chase 등으로 전환됐으면 순찰 목적지 덮어쓰지 않음
        if (_state != State.Idle || patrolPoints.Length == 0)
        {
            _patrolWaiting = false;
            yield break;
        }

        _patrolIndex = (_patrolIndex + 1) % patrolPoints.Length;
        if (_agent.enabled) _agent.SetDestination(patrolPoints[_patrolIndex].position);
        _patrolWaiting = false;
    }

    // ── 소리 감지 / 조사 ──────────────────────────────────────

    /// <summary>
    /// NoiseSystem 구독 핸들러. 판단은 전부 서버에서만 —
    /// 클라이언트는 목표 위치도 상태도 정할 수 없다.
    /// </summary>
    private void OnNoiseHeard(NoiseEvent noise)
    {
        if (!HasAuthority) return;

        // 격리 중에는 듣지 않는다. 죽었거나 기절 중에도 마찬가지.
        if (_state == State.Contained || _state == State.Dead || _state == State.Stunned) return;

        // 플레이어를 쫓는 중이면 소리보다 플레이어가 우선
        if (_state == State.Chase) return;
        if (_state == State.Attack && _target != null) return;

        float dist  = Vector3.Distance(transform.position, noise.position);
        float reach = noise.radius * Mathf.Max(0f, hearingMultiplier);
        if (dist > maxHearingRange) return;
        if (dist > reach) return;

        // 지금 때리고 있는 소음원이 낸 소리면 위치만 갱신하고 계속 때린다
        bool attackingThisSource = _state == State.Attack
                                && _target == null
                                && _noiseSource != null
                                && ReferenceEquals(noise.source, _noiseSource);

        // 이미 쫓고 있는 소리보다 덜 신경 쓰이면 무시한다.
        // (멀리서 총성을 듣고 달려가는 중에 옆의 발소리 한 번으로 방향이 꺾이지 않도록)
        // 같은 소음원이 계속 울리면 같은 점수로 다시 들어와 목표가 유지된다.
        float score = NoiseScore(noise, dist, reach);
        if (_hasNoise && !attackingThisSource && score < _noiseScore) return;

        _noiseScore    = score;
        _noisePosition = noise.position;
        _hasNoise      = true;
        _searchTimer   = searchDuration;

        // 소리를 낸 쪽이 무엇인지는 모른다 — 인터페이스로만 판단한다.
        // 계속 울리는 소음원이면 부술 대상 후보가 되고, IHittable이면 기존 공격 경로로 때린다.
        _noiseSource   = noise.source as INoiseSource;
        _noiseHittable = _noiseSource != null ? noise.source as IHittable : null;

        if (attackingThisSource) return;

        if (_state != State.Investigate) SetState(State.Investigate);
        else if (_agent.enabled) _agent.SetDestination(_noisePosition);
    }

    /// <summary>
    /// 소리에 얼마나 신경이 쓰이는가. 세기가 클수록, 가까울수록 높다.
    /// 들리는 한계 거리에서는 farNoiseWeight만큼만 반영되므로,
    /// 아주 가까이서 난 작은 소리가 멀리서 난 큰 소리를 이길 수 있다.
    /// </summary>
    private float NoiseScore(NoiseEvent noise, float dist, float reach)
    {
        if (reach <= 0f) return 0f;

        float closeness = Mathf.Clamp01(1f - dist / reach);   // 0 = 가장자리, 1 = 바로 옆
        return Mathf.Max(0f, noise.strength) * Mathf.Lerp(farNoiseWeight, 1f, closeness);
    }

    private void UpdateInvestigate()
    {
        // 목표가 없는데 Investigate에 남아 있으면 순찰로 되돌린다 (안전장치)
        if (!_hasNoise && !NoiseSourceActive()) { SetState(State.Idle); return; }

        // 쫓는 동안 관심이 서서히 식는다 — 이게 없으면 큰 소리를 한 번 들은 뒤로
        // 작은 소리에 영영 반응하지 않게 된다
        _noiseScore = Mathf.Max(0f, _noiseScore - interestDecayPerSecond * Time.deltaTime);

        // 플레이어 우선 — 소리를 쫓는 중에도 눈에 띄면 바로 추적으로 전환
        if (_detectionTimer <= 0f)
        {
            _detectionTimer = 0.4f;
            PlayerHealth player = FindNearestPlayer(detectRange);
            if (player != null)
            {
                _target = player;
                ClearNoiseTarget();
                SetState(State.Chase);
                return;
            }
        }

        // 아직 울리는 소음원이 있으면 그쪽을 계속 따라간다 (던져져 움직여도 추적)
        if (NoiseSourceActive())
        {
            _noisePosition = _noiseSource.NoisePosition;
            _searchTimer   = searchDuration;   // 소리가 나는 동안엔 조사 시간이 줄지 않는다

            // 사거리 밖이면 계속 접근, 안에 들어오면 부순다
            if (Vector3.Distance(transform.position, _noisePosition) <= noiseAttackRange)
            {
                SetState(State.Attack);
                return;
            }

            if (_agent.enabled) _agent.SetDestination(_noisePosition);
            return;
        }

        if (_agent.enabled && !_agent.pathPending) _agent.SetDestination(_noisePosition);

        if (!ArrivedAtNoise()) return;

        // 도착했는데 더 이상 소리가 없다 → 짧게 주변 조사 후 순찰 복귀
        _searchTimer -= Time.deltaTime;
        if (_searchTimer > 0f) return;

        Debug.Log($"[Monster] {name} 조사 종료 — 순찰 복귀");
        ClearNoiseTarget();
        SetState(State.Idle);
    }

    /// <summary>소리 지점에 도착했는가. 경로가 아예 닿지 않는 곳이면 도착으로 친다(무한 대기 방지).</summary>
    private bool ArrivedAtNoise()
    {
        if (Vector3.Distance(transform.position, _noisePosition) <= investigateArriveDistance) return true;
        if (!_agent.enabled || _agent.pathPending) return false;
        if (_agent.remainingDistance <= investigateArriveDistance) return true;

        return _agent.pathStatus != NavMeshPathStatus.PathComplete;
    }

    /// <summary>추적 중인 소음원이 아직 살아서 울리고 있는가.</summary>
    private bool NoiseSourceActive()
    {
        if (_noiseSource == null) return false;

        // 파괴·제거되어 Unity 오브젝트가 사라진 경우
        if (_noiseSource is UnityEngine.Object obj && obj == null)
        {
            _noiseSource   = null;
            _noiseHittable = null;
            return false;
        }

        return _noiseSource.IsMakingNoise;
    }

    private void ClearNoiseTarget()
    {
        _hasNoise      = false;
        _noiseScore    = 0f;
        _noiseSource   = null;
        _noiseHittable = null;
        _searchTimer   = 0f;
    }

    // ── Chase / 추적 ──────────────────────────────────────────

    private void UpdateChase()
    {
        if (_target == null || _target.IsDead) { _target = null; SetState(State.Idle); return; }

        float dist = Vector3.Distance(transform.position, _target.transform.position);

        // 놓쳤으면 그냥 순찰로 돌아가지 않고 마지막으로 본 자리를 조사한다.
        // (멀리서 저격당한 경우 Chase가 곧바로 풀리는데, 그래도 총소리가 난 쪽으로 다가온다)
        if (dist > loseRange)
        {
            ServerInvestigateLastKnown(_target.transform.position);
            return;
        }

        if (dist <= attackRange && _attackTimer <= 0f) { SetState(State.Attack);               return; }

        if (_agent.enabled) _agent.SetDestination(_target.transform.position);

        // 주기적으로 더 가까운 플레이어 재탐색
        if (_detectionTimer <= 0f)
        {
            _detectionTimer = 0.5f;
            PlayerHealth closer = FindNearestPlayer(detectRange);
            if (closer != null) _target = closer;
        }
    }

    // ── Attack / 공격 ─────────────────────────────────────────

    private void UpdateAttack()
    {
        // 플레이어가 아니라 소음원을 때리는 중
        if (_target == null && _noiseSource != null)
        {
            if (!NoiseSourceActive())
            {
                // 부쉈다(또는 소리가 멎었다) → 그 자리에서 짧게 조사
                Debug.Log($"[Monster] {name} 소음원 정지 확인 — 주변 조사");
                _searchTimer = searchDuration;
                SetState(State.Investigate);
                return;
            }

            _noisePosition = _noiseSource.NoisePosition;

            // 사거리를 벗어나면 다시 접근 (히스테리시스로 경계에서 떨리지 않게)
            if (Vector3.Distance(transform.position, _noisePosition) > noiseAttackRange + 0.5f)
            {
                SetState(State.Investigate);
                return;
            }

            if (_attackTimer > 0f) return;

            _attackTimer = attackCooldown;
            DoAttack();
            return;
        }

        if (_target == null || _target.IsDead)
        {
            _target = null;
            SetState(State.Idle);
            return;
        }

        // 거리 검사를 쿨다운보다 먼저 — 대상이 멀어지면 즉시 추적 재개
        float dist = Vector3.Distance(transform.position, _target.transform.position);
        if (dist > attackExitRange) { SetState(State.Chase); return; }

        if (_attackTimer > 0f) return;

        _attackTimer = attackCooldown;
        DoAttack();
    }

    private void DoAttack()
    {
        TriggerAttackAnim();
        // Animation Event 미사용 시 즉시 피해 (이벤트 사용 시 OnAttackHitFrame이 적용)
        if (!damageOnAnimationEvent) ApplyAttackDamage();
    }

    /// <summary>Attack 애니메이션의 타격 프레임 Animation Event에서 호출 (서버만 적용)</summary>
    public void OnAttackHitFrame()
    {
        if (!HasAuthority) return;
        if (!damageOnAnimationEvent) return;   // 즉시 피해 모드면 중복 방지
        ApplyAttackDamage();
    }

    private void ApplyAttackDamage()
    {
        // 소음원 타격 — 전용 파괴 함수를 만들지 않고 기존 피해 경로(IHittable.OnHit)를 그대로 탄다.
        // 대상의 HP·파괴 판정은 그 오브젝트가 서버 권위로 처리한다.
        if (_target == null)
        {
            if (_noiseHittable == null || !NoiseSourceActive()) return;

            _noiseHittable.OnHit(default, attackDamage, DamageType.Physical);
            Debug.Log($"[Monster] {name} → 소음원 공격 ({attackDamage} dmg)");
            return;
        }

        if (_target.IsDead) return;

        _target.TakeDamage(attackDamage);   // PlayerHealth가 서버 권위 + 동기화 처리
        Debug.Log($"[Monster] {name} → {_target.name} 공격 ({attackDamage} dmg)");
    }

    // ── IHittable: 피격 (서버에서 CmdDealDamage 경유로 호출) ──

    public void OnHit(RaycastHit hit, float damage, DamageType type = DamageType.Physical)
    {
        if (!HasAuthority) return;   // 원격 클라이언트는 무시 (서버 권위)
        if (_state == State.Contained || _state == State.Dead) return;

        // 때린 놈을 쫓는다. 스턴 처리보다 먼저 해야 StunRoutine이 복귀 상태로 Chase를 기억한다
        // (기절에서 깨어나면 자기를 쏜 쪽으로 향한다)
        ServerTargetAttacker();

        if (type == DamageType.Stun)
        {
            if (_state != State.Stunned) StartCoroutine(StunRoutine());
            return;
        }

        // 스턴 중에도 물리 피해는 정상 적용 (스턴 = 무적 아님)
        _currentHp -= damage;
        Debug.Log($"[Monster] {name} 피격 {damage:F0} dmg | HP {_currentHp:F0}/{maxHp}");

        if (_currentHp <= 0f) Die();
    }

    /// <summary>
    /// 추적을 포기하고 마지막으로 알던 위치를 조사한다.
    /// 소리 목표와 같은 배관을 쓰므로, 도착 후 아무것도 없으면 searchDuration만큼 둘러보고 순찰로 돌아간다.
    /// </summary>
    private void ServerInvestigateLastKnown(Vector3 position)
    {
        _target        = null;
        _noiseSource   = null;
        _noiseHittable = null;

        _noisePosition = position;
        _hasNoise      = true;
        _searchTimer   = searchDuration;
        _noiseScore    = 0f;   // 다른 소리가 들리면 언제든 목표를 넘겨줄 수 있게

        SetState(State.Investigate);
    }

    /// <summary>
    /// 자기를 때린 플레이어를 추적 대상으로 삼는다 (서버 전용).
    /// 공격자는 CmdDealDamage 실행 중에만 알 수 있으므로 OnHit 안에서 즉시 잡는다.
    ///
    /// loseRange 밖에서 저격당했으면 Chase가 곧바로 풀리므로, 그 경우 UpdateChase가
    /// 마지막 위치를 조사 목표로 넘긴다 — 결국 총을 쏜 자리로 다가온다.
    /// </summary>
    private void ServerTargetAttacker()
    {
        var inv = NetworkInteraction.CurrentAttackerInventory;

        PlayerHealth attacker = inv != null
            ? inv.GetComponentInChildren<PlayerHealth>(true) ?? inv.GetComponentInParent<PlayerHealth>()
            : null;

        // 싱글플레이어 폴백 (Mirror 미실행)
        if (attacker == null && !NetworkServer.active && !NetworkClient.active)
            attacker = PlayerInventory.LocalInstance != null
                ? PlayerInventory.LocalInstance.GetComponentInParent<PlayerHealth>()
                : null;

        if (attacker == null || attacker.IsDead) return;
        if (_target == attacker) return;

        _target = attacker;
        ClearNoiseTarget();

        // 이미 그 대상을 때리는 중이면 상태를 흔들지 않는다
        if (_state != State.Attack) SetState(State.Chase);

        Debug.Log($"[Monster] {name} 피격 — {attacker.name} 추적 시작");
    }

    private IEnumerator StunRoutine()
    {
        _stateBeforeStun = _state == State.Attack ? State.Chase : _state;
        SetState(State.Stunned);
        Debug.Log($"[Monster] {name} 스턴 {stunDuration}초");

        yield return new WaitForSeconds(stunDuration);

        if (_state != State.Stunned) yield break;
        SetState(_stateBeforeStun);   // 복귀 상태가 speed/isStopped 재설정
        Debug.Log($"[Monster] {name} 스턴 해제 → {_stateBeforeStun}");
    }

    private void Die()
    {
        Debug.Log($"[Monster] {name} 사망");
        SetState(State.Dead);
    }

    // ── 상태 전이 (서버) ──────────────────────────────────────

    private void SetState(State next)
    {
        _state = next;

        switch (next)
        {
            case State.Idle:
                if (_agent.enabled)
                {
                    _agent.speed = patrolSpeed;
                    _agent.isStopped = false;
                    if (patrolPoints.Length > 0)
                        _agent.SetDestination(patrolPoints[_patrolIndex].position);
                }
                break;

            case State.Investigate:
                if (_agent.enabled)
                {
                    _agent.speed     = chaseSpeed;   // 조사 이동 속도는 추적 속도를 재사용
                    _agent.isStopped = false;
                    _agent.SetDestination(_noisePosition);
                }
                break;

            case State.Chase:
                ClearNoiseTarget();   // 플레이어를 잡았으면 소리 목표는 버린다
                if (_agent.enabled)
                {
                    _agent.speed = chaseSpeed;
                    _agent.isStopped = false;
                }
                break;

            case State.Attack:
                StopAgent();   // 경로·속도 완전 제거 — 공격 중 한 걸음 이동 방지
                _attackTimer = attackCooldown;
                DoAttack();
                break;

            case State.Stunned:
                StopAgent();
                break;

            case State.Dead:
                _target = null;
                ClearNoiseTarget();
                StopAgent();
                if (_agent.enabled) _agent.enabled = false;
                break;
        }
    }

    /// <summary>NavMeshAgent 이동을 완전히 정지 (경로 + 잔여 속도 제거)</summary>
    private void StopAgent()
    {
        if (!_agent.enabled || !_agent.isOnNavMesh) return;
        _agent.isStopped = true;
        _agent.ResetPath();
        _agent.velocity = Vector3.zero;
    }

    // ── 비주얼/애니메이션 (호스트 + 모든 클라이언트) ─────────

    private void ApplyVisualState()
    {
        if (_state != _shownState)
        {
            OnEnterStateVisual(_state);
            _shownState = _state;
        }

        if (animator != null)
        {
            animator.SetFloat("Speed", _moveSpeed, 0.1f, Time.deltaTime);  // 1D Blend Tree
            animator.SetBool("IsStunned", _state == State.Stunned);
            animator.SetBool("IsDead",    _state == State.Dead);
        }
    }

    private void OnEnterStateVisual(State s)
    {
        if (s == State.Dead) SetCollidersEnabled(false);
    }

    /// <summary>공격 스윙 애니메이션 트리거 (서버는 RPC로 전파, 오프라인은 로컬)</summary>
    private void TriggerAttackAnim()
    {
        if (NetworkServer.active) RpcAttackAnim();
        else                      animator?.SetTrigger("Attack");
    }

    [ClientRpc]
    private void RpcAttackAnim() => animator?.SetTrigger("Attack");

    private void SetCollidersEnabled(bool on)
    {
        if (_colliders == null) return;
        foreach (var c in _colliders)
            if (c != null) c.enabled = on;
    }

    // ── 플레이어 탐색 (서버) ─────────────────────────────────

    private PlayerHealth FindNearestPlayer(float maxDist)
    {
        PlayerHealth nearest = null;
        float minDist = maxDist;

        foreach (var p in PlayerHealth.All)
        {
            if (p == null || p.IsDead) continue;
            float d = Vector3.Distance(transform.position, p.transform.position);
            if (d < minDist) { minDist = d; nearest = p; }
        }

        return nearest;
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, detectRange);
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, attackRange);
        Gizmos.color = Color.gray;
        Gizmos.DrawWireSphere(transform.position, loseRange);

        // 청력 상한 (실제 감지는 소리마다 다른 Detection Radius에도 함께 걸린다)
        Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.6f);
        Gizmos.DrawWireSphere(transform.position, maxHearingRange);

        if (_hasNoise)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(transform.position, _noisePosition);
            Gizmos.DrawWireSphere(_noisePosition, investigateArriveDistance);
        }
    }
}
