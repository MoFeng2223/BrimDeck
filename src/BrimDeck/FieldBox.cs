using System.Windows;
using System.Windows.Controls;

namespace BrimDeck;

// A text field in a frame that is drawn around it, such as a number box with its unit and steppers: a click anywhere in the frame
// that is not a control of its own (the padding, the unit, the divider) puts the focus in the field and selects its text, as a
// click on the field's own text would put it there. The window's "click on empty space releases the focus" rule leaves such a
// frame alone, or it would take the focus back on the same click.
internal static class FieldBox
{
    private static readonly DependencyProperty FieldProperty = DependencyProperty.RegisterAttached("Field", typeof(TextBox), typeof(FieldBox));

    public static void Attach(Border frame, TextBox field)
    {
        frame.SetValue(FieldProperty, field);
        // A stepper inside the frame handles its own press, so only the rest of the frame arrives here.
        frame.MouseLeftButtonDown += (_, e) =>
        {
            if (e.Handled) return;
            field.Focus(); field.SelectAll(); e.Handled = true;
        };
    }

    // Whether the element is a frame Attach was given.
    public static bool Owns(DependencyObject element) => element.GetValue(FieldProperty) is not null;
}
