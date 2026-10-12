using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

[MovedFrom(true, sourceNamespace: "", sourceAssembly: null, sourceClassName: "BoxMaskController")]
[ExecuteInEditMode]
public class URPLit_BoxMaskController_v02 : MonoBehaviour
{
    // どのオブジェクトを切り抜くかは、各マテリアルの [Toggle] _EnableBoxCut で制御する。
    // (対象にしたいマテリアルのインスペクタで Enable Box Cut を ON にしておく)

    private static readonly int BoxWorldToLocalID = Shader.PropertyToID("_BoxWorldToLocal");

    void Update()
    {
        // Boxの位置はシーン内で唯一なので、Renderer単位ではなく
        // グローバルシェーダー変数として一度だけセットする。
        // (MaterialPropertyBlock経由だとRendererがReflection Probe/Light Probeの
        //  通常のブレンド計算パスから外れ、環境反射が暗くなる副作用があったため)
        Shader.SetGlobalMatrix(BoxWorldToLocalID, transform.worldToLocalMatrix);
    }
}
