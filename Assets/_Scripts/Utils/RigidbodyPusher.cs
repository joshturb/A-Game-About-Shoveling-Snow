using UnityEngine;

public class RigidbodyPusher : MonoBehaviour
{
    [Header("Push")]
    public float pushPower = 2.0f;

    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        Rigidbody body = hit.collider.attachedRigidbody;

        if (body == null || body.isKinematic)
            return;

        if (hit.moveDirection.y < -0.3f)
            return;

        Vector3 pushDir = new(hit.moveDirection.x, 0f, hit.moveDirection.z);
        body.linearVelocity = pushDir * pushPower;
    }
}
