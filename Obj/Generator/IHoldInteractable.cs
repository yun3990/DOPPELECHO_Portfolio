/// <summary>
/// 꾹 누르기 상호작용 인터페이스
/// ChargeButton처럼 누르고 있는 동안 계속 동작해야 하는 경우
/// </summary>
public interface IHoldInteractable
{
    string InteractPrompt { get; }
    bool CanInteract { get; }

    void OnHoldStart();   // 누르기 시작
    void OnHoldStay();    // 누르는 중 (매 프레임)
    void OnHoldEnd();     // 손 뗌
}