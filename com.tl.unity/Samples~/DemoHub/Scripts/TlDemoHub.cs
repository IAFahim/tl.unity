using UnityEngine;
using UnityEngine.SceneManagement;

#if UNITY_7000_0_OR_NEWER
[Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
public sealed class TlDemoHub : MonoBehaviour
{
    public string[] Demos = new string[0];
    public string[] Notes = new string[0];

    static TlDemoHub s_Back;
    GUIStyle _title;
    GUIStyle _note;

    void Awake()
    {
        if (s_Back != null)
        {
            Destroy(gameObject);
            return;
        }
        s_Back = this;
        DontDestroyOnLoad(gameObject);
    }

    void OnDestroy()
    {
        if (s_Back == this)
            s_Back = null;
    }

    void OnGUI()
    {
        _title ??= new GUIStyle(GUI.skin.label) { fontSize = 28, normal = { textColor = Color.white } };
        _note ??= new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true, normal = { textColor = new Color(0.85f, 0.85f, 0.85f) } };
        if (SceneManager.GetActiveScene().buildIndex != 0)
        {
            if (GUI.Button(new Rect(Screen.width - 136, 12, 120, 34), "Tl Demos"))
                SceneManager.LoadScene(0);
            return;
        }
        GUI.Label(new Rect(32, 24, 800, 40), "Tl Demos", _title);
        for (var i = 0; i < Demos.Length; i++)
        {
            var y = 84 + i * 64;
            if (GUI.Button(new Rect(32, y, 220, 44), Demos[i]))
                SceneManager.LoadScene(i + 1);
            if (i < Notes.Length)
                GUI.Label(new Rect(268, y, 700, 56), Notes[i], _note);
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        s_Back = null;
    }
}
