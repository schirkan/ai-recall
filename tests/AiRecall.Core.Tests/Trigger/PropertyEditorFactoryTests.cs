using System.ComponentModel;
using AiRecall.Trigger;
using Xunit;

namespace AiRecall.Core.Tests.Trigger;

public class PropertyEditorFactoryTests
{
    [Fact]
    public void GetEditor_Bool_ReturnsCheckBox()
    {
        var info = PropertyEditorFactory.GetEditor(MakeProp(typeof(FakeConfig), "BoolProp", true));
        Assert.Equal(PropertyEditorFactory.EditorKind.CheckBox, info.Kind);
        Assert.Equal("true", info.DisplayText);
        Assert.True((bool)info.Parser!("true")!);
    }

    [Fact]
    public void GetEditor_Int_ReturnsTextBox()
    {
        var info = PropertyEditorFactory.GetEditor(MakeProp(typeof(FakeConfig), "IntProp", 42));
        Assert.Equal(PropertyEditorFactory.EditorKind.TextBox, info.Kind);
        Assert.Equal("42", info.DisplayText);
        Assert.Equal(123, info.Parser!("123"));
    }

    [Fact]
    public void GetEditor_String_ReturnsTextBox()
    {
        var info = PropertyEditorFactory.GetEditor(MakeProp(typeof(FakeConfig), "StringProp", "hello"));
        Assert.Equal(PropertyEditorFactory.EditorKind.TextBox, info.Kind);
        Assert.Equal("hello", info.DisplayText);
    }

    [Fact]
    public void GetEditor_Enum_ReturnsComboBox()
    {
        var info = PropertyEditorFactory.GetEditor(MakeProp(typeof(FakeConfig), "EnumProp", FakeEnum.B));
        Assert.Equal(PropertyEditorFactory.EditorKind.ComboBox, info.Kind);
        Assert.Equal("B", info.DisplayText);
        Assert.Equal(FakeEnum.A, info.Parser!("a"));
    }

    [Fact]
    public void GetEditor_ListString_ReturnsListStringTextBox()
    {
        var list = new List<string> { "a", "b", "c" };
        var info = PropertyEditorFactory.GetEditor(MakeProp(typeof(FakeConfig), "ListProp", list));
        Assert.Equal(PropertyEditorFactory.EditorKind.ListStringTextBox, info.Kind);
        Assert.Equal("a, b, c", info.DisplayText);
        var parsed = (List<string>)info.Parser!("x, y, z")!;
        Assert.Equal(new[] { "x", "y", "z" }, parsed);
    }

    [Fact]
    public void GetEditor_ReadOnly_ReturnsReadOnlyKind()
    {
        var info = PropertyEditorFactory.GetEditor(MakeProp(typeof(FakeConfig), "ReadOnlyProp", "x", readOnly: true));
        Assert.Equal(PropertyEditorFactory.EditorKind.ReadOnly, info.Kind);
        Assert.Null(info.Parser);
    }

    [Fact]
    public void GetEditor_UnsupportedType_ReturnsReadOnlyKind()
    {
        var info = PropertyEditorFactory.GetEditor(MakeProp(typeof(FakeConfig), "GuidProp", Guid.NewGuid()));
        Assert.Equal(PropertyEditorFactory.EditorKind.ReadOnly, info.Kind);
    }

    private static PropertyDescriptor MakeProp(Type type, string name, object value, bool readOnly = false)
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
        // We need a property whose value comes from the instance we set
        return new InstancePropertyDescriptor(instance, prop);
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
    private sealed class InstancePropertyDescriptor : PropertyDescriptor
    {
        private readonly PropertyDescriptor _inner;
        private readonly object _instance;
        public InstancePropertyDescriptor(object instance, PropertyDescriptor inner) : base(inner)
        {
            _instance = instance;
            _inner = inner;
        }
        public override bool IsReadOnly => _inner.IsReadOnly;
        public override Type PropertyType => _inner.PropertyType;
        public override Type ComponentType => _inner.ComponentType;
        public override object? GetValue(object? component) => _inner.GetValue(_instance);
        public override void SetValue(object? component, object? value) => _inner.SetValue(_instance, value);
        public override bool CanResetValue(object component) => _inner.CanResetValue(_instance);
        public override void ResetValue(object component) => _inner.ResetValue(_instance);
        public override bool ShouldSerializeValue(object component) => _inner.ShouldSerializeValue(_instance);
    }
}