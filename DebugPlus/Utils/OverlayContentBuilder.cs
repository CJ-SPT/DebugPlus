using System.Text;
using UnityEngine;

namespace DebugPlus.Utils;

public static class OverlayContentBuilder
{
	private static readonly StringBuilder Sb = new();
	
	/// <summary>
	/// Appends a new line to the GUI rect
	/// </summary>
	/// <param name="label">Label (key)</param>
	/// <param name="data">Data (value)</param>
	/// <param name="labelColor">Color of the label</param>
	/// <param name="dataColor">Color of the data</param>
	/// <param name="labelEnabled">Is the label enabled</param>
	public static void AppendLabeledValue(string label, string data, Color labelColor, Color dataColor, bool labelEnabled = true)
	{
		var labelColorString = GetColorString(labelColor);
		var dataColorString = GetColorString(dataColor);

		AppendLabeledValue(label, data, labelColorString, dataColorString, labelEnabled);
	}

	public new static string ToString()
	{
		return Sb.ToString();
	}
	
	public static void Clear()
	{
		Sb.Clear();
	}
	
	private static void AppendLabeledValue(string label, string data, string labelColor, string dataColor, bool labelEnabled = true)
	{
		if (labelEnabled)
		{
			Sb.AppendFormat("<color={0}>{1}:</color>", labelColor, label);
		}

		Sb.AppendFormat(" <color={0}>{1}</color>\n", dataColor, data);
	}
	
	private static string GetColorString(Color color)
	{
		if (color == Color.black) return "black";
		if (color == Color.white) return "white";
		if (color == Color.yellow) return "yellow";
		if (color == Color.red) return "red";
		if (color == Color.green) return "green";
		if (color == Color.blue) return "blue";
		if (color == Color.cyan) return "cyan";
		if (color == Color.magenta) return "magenta";
		if (color == Color.gray) return "gray";
		if (color == Color.clear) return "clear";
		return "#" + ColorUtility.ToHtmlStringRGB(color);
	}
}