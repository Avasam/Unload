using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace unload;

public static partial class TextBoxValidator
{
    // Ensures a textbox only allows integer inputs
    public static void ClampInteger(TextBox textBox, int minValue, int maxValue, string formatting = "")
    {
        if (!string.IsNullOrEmpty(textBox.Text))
        {
            int value = Math.Clamp(int.Parse(textBox.Text), minValue, maxValue);
            textBox.Text = value.ToString(formatting);
        }
    }

    // Ensures a textbox only allows double inputs
    public static void ClampDouble(TextBox textBox, double minValue, double maxValue, string formatting = "")
    {
        if (!string.IsNullOrEmpty(textBox.Text))
        {
            double value = Math.Clamp(double.Parse(textBox.Text), minValue, maxValue);
            textBox.Text = value.ToString(formatting);
        }
    }

    private static readonly Regex nonInteger = NonInteger();
    private static readonly Regex nonDouble = NonDouble();

    // Prevents anything other than a round number from being typed or pasted in the text box
    public static void ForceInteger(TextBox textBox) => RejectInput(textBox, nonInteger);

    // Prevents anything other than any number from being typed or pasted in the text box
    public static void ForceDouble(TextBox textBox) => RejectInput(textBox, nonDouble);

    private static void RejectInput(TextBox textBox, Regex disallowed)
    {
        textBox.PreviewTextInput += (sender, e) => e.Handled = disallowed.IsMatch(e.Text);

        // Pasting doesn't go through PreviewTextInput
        DataObject.AddPastingHandler(textBox, (sender, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || disallowed.IsMatch(text))
            {
                e.CancelCommand();
            }
        });
    }

    [GeneratedRegex("[^0-9]")]
    private static partial Regex NonInteger();

    [GeneratedRegex("[^0-9.]")]
    private static partial Regex NonDouble();
}
