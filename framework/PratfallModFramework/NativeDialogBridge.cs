using System.Reflection;
using Godot;

namespace PratfallModFramework;

internal static class NativeDialogBridge
{
    private static object? _activeController;
    private static bool _suppressActiveCallback;

    public static bool TryShow(
        SceneTree? tree,
        string title,
        string message,
        string okButtonText,
        string cancelButtonText,
        bool hideCancelButton,
        Action<bool> onComplete)
    {
        if (tree?.Root == null)
            return false;

        var controller = FindNodeByName(tree.Root, "DialogUI");
        if (controller == null)
            return false;

        var controllerType = controller.GetType();
        var optionsType = controllerType.Assembly.GetType("DialogUIShowOptions");
        if (optionsType == null)
            return false;

        var options = Activator.CreateInstance(optionsType);
        if (options == null)
            return false;

        SetField(optionsType, options, "Title", title);
        SetField(optionsType, options, "Message", message);
        SetField(optionsType, options, "OkButtonText", okButtonText);
        SetField(optionsType, options, "CancelButtonText", cancelButtonText);
        SetField(optionsType, options, "HideCancelButton", hideCancelButton);
        SetField(optionsType, options, "StayOpenOnOk", false);
        SetField(optionsType, options, "DontShowAsOverlay", false);
        SetField(optionsType, options, "ShowTexture", false);
        SetField(optionsType, options, "Texture", null);

        var showMethod = controllerType.GetMethod(
            "Show",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { optionsType, typeof(Action<bool>) },
            modifiers: null);
        if (showMethod == null)
            return false;

        _activeController = controller;
        _suppressActiveCallback = false;

        Action<bool> wrappedOnComplete = accepted =>
        {
            var suppressCallback = _suppressActiveCallback;
            _activeController = null;
            _suppressActiveCallback = false;

            if (!suppressCallback)
                onComplete(accepted);
        };

        showMethod.Invoke(controller, new object?[] { options, wrappedOnComplete });
        return true;
    }

    public static void DismissActive()
    {
        if (_activeController == null)
            return;

        var controller = _activeController;
        var cancelMethod = controller.GetType().GetMethod(
            "OnCancelButtonClicked",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (cancelMethod == null)
            return;

        _suppressActiveCallback = true;
        cancelMethod.Invoke(controller, parameters: null);
    }

    private static void SetField(Type type, object instance, string fieldName, object? value)
    {
        var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        field?.SetValue(instance, value);
    }

    private static Node? FindNodeByName(Node node, string name)
    {
        if (node.Name == name)
            return node;

        foreach (Node child in node.GetChildren())
        {
            var match = FindNodeByName(child, name);
            if (match != null)
                return match;
        }

        return null;
    }
}
