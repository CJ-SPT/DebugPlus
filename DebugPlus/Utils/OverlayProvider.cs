using UnityEngine;

namespace DebugPlus.Utils;

/// <summary>
/// Credits: DrakiaXYZ for the overlay code
/// </summary>
public class OverlayProvider : MonoBehaviour
{
	private GUIStyle guiStyle;
	private float _screenScale = 1.0f;

	private GUIContent _content = new();
	private Rect _rect = new();
	
	private void Awake()
	{
		// If DLSS or FSR are enabled, set a screen scale value
		if (!CameraClass.Instance.SSAA.isActiveAndEnabled) return;
		
		_screenScale = (float)CameraClass.Instance.SSAA.GetOutputWidth() / (float)CameraClass.Instance.SSAA.GetInputWidth();
	}
	
	private void OnGUI()
	{
		if (guiStyle is null)
		{
			CreateGuiStyle();
		}
		
		var pos = transform.position;
		var dist = Mathf.RoundToInt((transform.position - Camera.main!.transform.position).magnitude);
		
		if (_content.text.Length <= 0 || !(dist < 200f)) return;
		
		var screenPos = Camera.main!.WorldToScreenPoint(pos + (Vector3.up * 1.5f));
		
		// Don't render behind the camera.
		if (screenPos.z <= 0) return;
		
		SetRectSize(screenPos);
		
		GUI.Box(_rect, _content, guiStyle);
	}

	public void SetOverlayContent(string content)
	{
		_content.text = content;
	}
	
	/// <summary>
	/// Sets the rect size for the overlay to render. Should be called in the implementing classes OnGUI()
	/// </summary>
	/// <param name="screenPos">Position on the screen</param>
	/// <param name="content">Content of the GUI</param>
	/// <param name="rect">Rect to modify</param>
	private void SetRectSize(Vector3 screenPos)
	{
		var guiSize = guiStyle.CalcSize(_content);
		_rect.x = (screenPos.x * _screenScale) - (guiSize.x / 2);
		_rect.y = Screen.height - ((screenPos.y * _screenScale) + guiSize.y);
		_rect.size = guiSize;
	}
	
	private void CreateGuiStyle()
	{
		guiStyle = new GUIStyle(GUI.skin.box)
		{
			alignment = TextAnchor.MiddleLeft,
			fontSize = 16, // TODO: Add config for font size
			margin = new RectOffset(3, 3, 3, 3),
			richText = true
		};
	}
}