using System.IO;
using LoLKarmaBook.Desktop.Infrastructure;

namespace LoLKarmaBook.Desktop.ViewModels;

public enum StatusSeverity
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>One visible, dismissible status line. Errors stay until replaced.</summary>
public sealed class StatusMessageViewModel : ObservableObject
{
    private string _message = string.Empty;
    private StatusSeverity _severity = StatusSeverity.Info;

    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public StatusSeverity Severity
    {
        get => _severity;
        private set
        {
            if (SetProperty(ref _severity, value))
            {
                OnPropertyChanged(nameof(IsError));
                OnPropertyChanged(nameof(IsWarning));
            }
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public bool IsError => Severity == StatusSeverity.Error;

    public bool IsWarning => Severity == StatusSeverity.Warning;

    public void Show(string message, StatusSeverity severity = StatusSeverity.Info)
    {
        Message = message;
        Severity = severity;
    }

    public void Success(string message) => Show(message, StatusSeverity.Success);

    public void Warn(string message) => Show(message, StatusSeverity.Warning);

    public void Error(string message) => Show(message, StatusSeverity.Error);

    public void Clear()
    {
        Message = string.Empty;
        Severity = StatusSeverity.Info;
    }

    /// <summary>Human-friendly text for unexpected failures, without stack traces.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "无法访问文件：权限不足或文件被占用。",
        IOException => "读写本地文件失败：" + exception.Message,
        System.Security.Cryptography.CryptographicException =>
            "数据解密失败：文件可能来自其他 Windows 用户或已损坏。",
        PlatformNotSupportedException => exception.Message,
        _ => exception.Message
    };

    public void Report(Exception exception, string context, DiagnosticLog? log = null)
    {
        log?.Error("ui", context + ": " + exception.GetType().Name, exception);
        Error(context + "：" + Describe(exception));
    }
}
