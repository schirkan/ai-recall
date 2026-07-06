using System.ComponentModel;
using AiRecall.Trigger;
using Xunit;

namespace AiRecall.Core.Tests.Trigger;

public class PropertyEditorFactoryTests
{
    [Fact]
    public void GetEditor_Bool_ReturnsCheckBox()
    {
        var (prop, instance) = MakePropWithInstance("BoolProp", true);
        var info = PropertyEditorFactory.GetEditor(prop, instance);
        Assert.Equal(PropertyEditorFactory.EditorKind.CheckBox, info.Kind);
        Assert.Equal("true", info.DisplayText);
        Assert.True((bool)info.Parser!("true")!);
    }

    [Fact]
    public void GetEditor_Int_ReturnsTextBox()
    {
        var (prop, instance) = MakePropWithInstance("IntProp", 42);
        var info = PropertyEditorFactory.GetEditor(prop, instance);
        Assert.Equal(PropertyEditorFactory.EditorKind.TextBox, info.Kind);
        Assert.Equal("42", info.DisplayText);
        Assert.Equal(123, info.Parser!("123"));
    }

    [Fact]
    public void GetEditor_String_ReturnsTextBox()
    {
        var (prop, instance) = MakePropWithInstance("StringProp", "hello");
        var info = PropertyEditorFactory.GetEditor(prop, instance);
        Assert.Equal(PropertyEditorFactory.EditorKind.TextBox, info.Kind);
        Assert.Equal("hello", info.DisplayText);
    }

    [Fact]
    public void GetEditor_Enum_ReturnsComboBox()
    {
        var (prop, instance) = MakePropWithInstance("EnumProp", FakeEnum.B);
        var info = PropertyEditorFactory.GetEditor(prop, instance);
        Assert.Equal(PropertyEditorFactory.EditorKind.ComboBox, info.Kind);
        Assert.Equal("B", info.DisplayText);
        Assert.Equal(FakeEnum.A, info.Parser!("a"));
    }

    [Fact]
    public void GetEditor_ListString_ReturnsListStringTextBox()
    {
        var list = new List<string> { "a", "b", "c" };
        var (prop, instance) = MakePropWithInstance("ListProp", list);
        var info = PropertyEditorFactory.GetEditor(prop, instance);
        Assert.Equal(PropertyEditorFactory.EditorKind.ListStringTextBox, info.Kind);
        Assert.Equal("a, b, c", info.DisplayText);
        var parsed = (List<string>)info.Parser!("x, y, z")!;
        Assert.Equal(new[] { "x", "y", "z" }, parsed);
    }

    [Fact]
    public void GetEditor_ReadOnly_ReturnsReadOnlyKind()
    {
        var (prop, instance) = MakePropWithInstance("ReadOnlyProp", "x", readOnly: true);
        var info = PropertyEditorFactory.GetEditor(prop, instance);
        Assert.Equal(PropertyEditorFactory.EditorKind.ReadOnly, info.Kind);
        Assert.Null(info.Parser);
    }

    [Fact]
    public void GetEditor_UnsupportedType_ReturnsReadOnlyKind()
    {
        var (prop, instance) = MakePropWithInstance("GuidProp", Guid.NewGuid());
        var info = PropertyEditorFactory.GetEditor(prop, instance);
        Assert.Equal(PropertyEditorFactory.EditorKind.ReadOnly, info.Kind);
    }

    // ----- Nullable<T> (Bug-Bash 2026-07-06 I-20) -----

    [Fact]
    public void GetEditor_NullableBool_WithValue_ReturnsCheckbox()
    {
        var (prop, instance) = MakePropWithInstance("NullableBoolProp", (bool?)true);
        var info = PropertyEditorFactory.GetEditor(prop, instance);
        Assert.Equal(PropertyEditorFactory.EditorKind.CheckBox, info.Kind);
        Assert.Equal("true", info.DisplayText);
    }

    [Fact]
    public void GetEditor_NullableBool_NullValue_DoesNotShowNullableLabel()
    {
        // Regressions-Test: vor dem Fix wurde "(Nullable`1)" angezeigt.
        var (prop, instance) = MakePropWithInstance("NullableBoolProp", (bool?)null);
        var info = PropertyEditorFactory.GetEditor(prop, instance);
        Assert.NotEqual(PropertyEditorFactory.EditorKind.ReadOnly, info.Kind);
        Assert.Equal("null", info.DisplayText);
        // Beim Save wird der CheckBox "true"/"false" liefern, nicht "null" —
        // das ist der bekannte Trade-off ohne echtes Tri-State-UI. Der User
        // sieht also, dass der Wert null war (durch "null"-Label), aber beim
        // Editieren verliert er den null-Zustand. Fuer MVP akzeptabel.
        Assert.True((bool?)info.Parser!("false")! == false);
    }

    [Fact]
    public void GetEditor_NullableInt_NullValue_EmptyStringReturnsNull()
    {
        var (prop, instance) = MakePropWithInstance("NullableIntProp", (int?)null);
        var info = PropertyEditorFactory.GetEditor(prop, instance);
        Assert.Equal(PropertyEditorFactory.EditorKind.TextBox, info.Kind);
        Assert.Equal("null", info.DisplayText);
        Assert.Null(info.Parser!(""));
        Assert.Null(info.Parser!("null"));
        Assert.Equal(99, info.Parser!("99"));
    }

    [Fact]
    public void GetEditor_NullableEnum_NullValue_EmptyStringReturnsNull()
    {
        var (prop, instance) = MakePropWithInstance("NullableEnumProp", (FakeEnum?)null);
        var info = PropertyEditorFactory.GetEditor(prop, instance);
        Assert.Equal(PropertyEditorFactory.EditorKind.ComboBox, info.Kind);
        Assert.Equal("null", info.DisplayText);
        Assert.Null(info.Parser!(""));
        Assert.Equal(FakeEnum.A, info.Parser!("A"));
    }

    /// <summary>
    /// I-1 Regressions-Test (Bug-Bash 2026-07-05): Der Editor MUSS die Instance-Werte
    /// sehen, NICHT die Type-Defaults. Wenn das Production-Code-Pattern
    /// (prop.GetValue(null)) wieder eingeführt wird, bricht dieser Test.
    /// </summary>
    [Fact]
    public void GetEditor_ReadsInstanceValue_NotDefault()
    {
        // Instance hat ungewöhnliche Werte — wenn der Editor "0" / "" / "false"
        // zurückgibt, liest er die Defaults statt der Instance.
        var instance = new FakeConfig { IntProp = 12345, StringProp = "instance-value", BoolProp = true };
        var intProp = TypeDescriptor.GetProperties(instance).Find("IntProp", ignoreCase: false)!;
        var strProp = TypeDescriptor.GetProperties(instance).Find("StringProp", ignoreCase: false)!;
        var boolProp = TypeDescriptor.GetProperties(instance).Find("BoolProp", ignoreCase: false)!;

        Assert.Equal("12345", PropertyEditorFactory.GetEditor(intProp, instance).DisplayText);
        Assert.Equal("instance-value", PropertyEditorFactory.GetEditor(strProp, instance).DisplayText);
        Assert.Equal("true", PropertyEditorFactory.GetEditor(boolProp, instance).DisplayText);
    }

    private static (PropertyDescriptor prop, FakeConfig instance) MakePropWithInstance(string name, object? value, bool readOnly = false)
    {
        var instance = new FakeConfig();
        var prop = TypeDescriptor.GetProperties(instance).Find(name, ignoreCase: false)!;
        if (readOnly)
        {
            prop = new ReadOnlyPropertyWrapper(prop);
        }
        else
        {
            prop.SetValue(instance, value);
        }
        // Echte PropertyDescriptor zurückgeben (instance-bound via TypeDescriptor.GetProperties(instance)).
        // KEIN InstancePropertyDescriptor-Wrapper mehr — der hat den Bug-bash-Fund maskiert.
        return (prop, instance);
    }

    private enum FakeEnum { A, B, C }

    private sealed class FakeConfig
    {
        public bool BoolProp { get; set; }
        public int IntProp { get; set; }
        public string? StringProp { get; set; }
        public FakeEnum EnumProp { get; set; }
        public List<string> ListProp { get; set; } = new();
        public Guid GuidProp { get; set; }
        public string ReadOnlyProp => "fixed";

        // Bug-Bash 2026-07-06 I-20: Nullable-Properties fuer Tests der
        // neuen Null-Behandlung (vorher: "(Nullable`1)" als ReadOnly-Label).
        public bool? NullableBoolProp { get; set; }
        public int? NullableIntProp { get; set; }
        public string? NullableStringProp { get; set; }
        public FakeEnum? NullableEnumProp { get; set; }
    }

    /// <summary>
    /// Wraps a PropertyDescriptor to make it read-only (we can't decorate FakeConfig
    /// with [ReadOnly(true)] without polluting real types).
    /// </summary>
    private sealed class ReadOnlyPropertyWrapper : PropertyDescriptor
    {
        private readonly PropertyDescriptor _inner;
        public ReadOnlyPropertyWrapper(PropertyDescriptor inner) : base(inner) { _inner = inner; }
        public override bool IsReadOnly => true;
        public override Type PropertyType => _inner.PropertyType;
        public override object? GetValue(object? component) => _inner.GetValue(component);
        public override void SetValue(object? component, object? value) { /* no-op */ }
        public override bool CanResetValue(object component) => false;
        public override void ResetValue(object component) { }
        public override bool ShouldSerializeValue(object component) => false;
        public override Type ComponentType => _inner.ComponentType;
    }

    /// <summary>
    /// Wraps a PropertyDescriptor so that GetValue/SetValue operate on a specific
    /// instance (FakeConfig) — the original TypeDescriptor.GetProperties(FakeConfig)
    /// uses the instance's actual values, but we need a clean way to set a value
    /// and then get the right value back.
    /// </summary>
}