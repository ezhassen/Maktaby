using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Maktaby.Helpers;
using System.Runtime.CompilerServices;

namespace Maktaby.ViewModels;

/// <summary>
/// Base class for view models
/// </summary>
public abstract class ViewModelBase : ObservableValidator
{
    public bool IsInDesignMode => HelperUI.IsInDesignMode;
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

/// <summary>
/// Base class for view models with data load
/// </summary>
public abstract class ViewModelWithLoadBase : ViewModelBase
{
    //internal readonly ILogger logger;

    //public ViewModelWithLoadBase()
    //{
    //logger = Logging.Log;
    //}
    //public event PropertyChangedEventHandler? PropertyChanged;

    //protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    //    => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    #region props

    bool isWorking;
    public bool IsWorking
    {
        get { return isWorking; }
        set
        {
            if (SetProperty(ref isWorking, value))
            {
                OnPropertyChanged(nameof(IsNotWorking)); //uses CallerMemberName
            }
        }
    }
    public bool IsNotWorking => !IsWorking;


    bool _IsLoaded;
    public bool IsLoaded
    {
        get { return _IsLoaded; }
        set
        {
            if (_IsLoaded != value)
            {
                _IsLoaded = value;
                OnPropertyChanged(); //uses CallerMemberName
            }
        }
    }

    //[ObservableProperty]


    #endregion

    protected virtual void OnLoadDataDesignTime()
    {

    }

    protected virtual Task OnLoadDataRunTimeAsync()
    {
        return Task.CompletedTask;
    }


    protected virtual bool CanLoadData() => !IsWorking;
    IAsyncRelayCommand? _LoadDataCommand;
    public IAsyncRelayCommand LoadDataCommand => _LoadDataCommand ??= new AsyncRelayCommand(LoadDataAsync, CanLoadData);
    //[RelayCommand(CanExecute = nameof(CanLoadData))]
    protected async Task LoadDataAsync()
    {
        try
        {
            if (HelperUI.IsInDesignMode)
            {
                OnLoadDataDesignTime();
                return;
            }
            //throw new Exception("Test Exception");
            IsWorking = true;
            Logging.Log.Verbose($"{GetType().Name} LoadData Started");
            await OnLoadDataRunTimeAsync();
            Logging.Log.Verbose($"{GetType().Name} LoadData Ended Successfully");
        }
        catch (Exception ex)
        {
            Logging.Log.Error(ex, $"{GetType().Name} LoadData Error");
            throw;
        }
        finally
        {
            IsWorking = false;
            IsLoaded = true;
        }
    }

    #region DoWork

    /// <summary>
    /// Performs an action that is between isWorking conditions (sets it to true before action then false after it finishes)
    /// </summary>
    /// <param name="action">work to be done</param>
    /// <param name="checkIsLoaded">if <see cref="IsLoaded"/>==false then ignore the action?</param>
    /// <param name="checkIsWorking">if <see cref="IsWorking"/>==true then ignore the action?</param>
    protected void DoWork(Action action, bool checkIsLoaded = true, bool checkIsWorking = true, Action? afterSuccessfulAction = null)
    {
        if (action is null) return;
        if (checkIsLoaded && !IsLoaded) return;
        if (checkIsWorking && IsWorking) return;
        try
        {
            IsWorking = true;
            action?.Invoke();
            IsWorking = false;
            afterSuccessfulAction?.Invoke();
        }
        finally
        {
            IsWorking = false;
        }
    }
    /// <summary>
    /// Performs an action that is between isWorking conditions (sets it to true before action then false after it finishes)
    /// </summary>
    /// <param name="func">work to be done</param>
    /// <param name="checkIsLoaded">if <see cref="IsLoaded"/>==false then ignore the action?</param>
    /// <param name="checkIsWorking">if <see cref="IsWorking"/>==true then ignore the action?</param>
    protected T? DoWork<T>(Func<T?> func, bool checkIsLoaded = true, bool checkIsWorking = true, Action? afterSuccessfulAction = null)
    {
        if (func is null) return default;
        if (checkIsLoaded && !IsLoaded) return default;
        if (checkIsWorking && IsWorking) return default;
        T? res = default;
        try
        {
            IsWorking = true;
            res = func();
            IsWorking = false;
            afterSuccessfulAction?.Invoke();
        }
        finally
        {
            IsWorking = false;
        }
        return res;
    }

    /// <summary>
    /// Performs an action that is between isWorking conditions (sets it to true before action then false after it finishes). (using Async)
    /// </summary>
    /// <param name="actionAsync">work to be done</param>
    /// <param name="checkIsLoaded">if <see cref="IsLoaded"/>==false then ignore the action?</param>
    /// <param name="checkIsWorking">if <see cref="IsWorking"/>==true then ignore the action?</param>
    protected async Task DoWork(Func<Task> actionAsync, bool checkIsLoaded = true, bool checkIsWorking = true, Action? afterSuccessfulAction = null)
    {
        if (actionAsync is null) return;
        if (checkIsLoaded && !IsLoaded) return;
        if (checkIsWorking && IsWorking) return;
        try
        {
            IsWorking = true;
            await actionAsync();
            IsWorking = false;
            afterSuccessfulAction?.Invoke();
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>
    /// Performs an action that is between isWorking conditions (sets it to true before action then false after it finishes). (using Async)
    /// </summary>
    /// <param name="funcAsync">work to be done</param>
    /// <param name="checkIsLoaded">if <see cref="IsLoaded"/>==false then ignore the action?</param>
    /// <param name="checkIsWorking">if <see cref="IsWorking"/>==true then ignore the action?</param>
    protected async Task<T?> DoWork<T>(Func<Task<T?>> funcAsync, bool checkIsLoaded = true, bool checkIsWorking = true, Action? afterSuccessfulAction = null)
    {
        if (funcAsync is null) return default;
        if (checkIsLoaded && !IsLoaded) return default;
        if (checkIsWorking && IsWorking) return default;
        T? res = default;
        try
        {
            IsWorking = true;
            res = await funcAsync();
            IsWorking = false;
            afterSuccessfulAction?.Invoke();
        }
        finally
        {
            IsWorking = false;
        }
        return res;
    }
    #endregion
}

/// <summary>
/// Base class for view models with data load and window commands
/// </summary>
public abstract class ViewModelWindowBase : ViewModelWithLoadBase
{

    bool _requestWindowClose;
    public bool RequestWindowClose
    {
        get { return _requestWindowClose; }
        set
        {
            SetProperty(ref _requestWindowClose, value);
        }
    }

    bool _IsOK;
    public bool IsOK
    {
        get { return _IsOK; }
        set
        {
            if (_IsOK != value)
            {
                _IsOK = value;
                OnPropertyChanged(); //uses CallerMemberName
            }
        }
    }

    string _DefaultStatuesText = "---";
    public string DefaultStatuesText
    {
        get { return _DefaultStatuesText; }
        set
        {
            if (_DefaultStatuesText != value)
            {
                _DefaultStatuesText = value;
                OnPropertyChanged(); //uses CallerMemberName
            }
        }
    }

    string _StatuesText = "Ready";
    public string StatuesText
    {
        get { return _StatuesText; }
        set
        {
            if (_StatuesText != value)
            {
                _StatuesText = value;
                OnPropertyChanged(); //uses CallerMemberName
            }
        }
    }

    public void ResetStatuesText()
    {
        StatuesText = DefaultStatuesText;
    }
    public void ResetStatuesTextAfterTime(TimeSpan timeSpan)
    {
        HelperUI.FireOnceAfterTime(timeSpan, () =>
        {
            if (!IsWorking) ResetStatuesText();
        });
    }

    #region Window commands

    /// <summary>
    /// can requestWindowClose
    /// </summary>
    /// <returns></returns>
    protected virtual bool CanCancel() => !IsWorking;

    IRelayCommand? _CancelCommand;
    public IRelayCommand CancelCommand => _CancelCommand ??= new CommunityToolkit.Mvvm.Input.RelayCommand(OnCancel, CanCancel);
    /// <summary>
    /// requestWindowClose
    /// </summary>
    //[RelayCommand(CanExecute = nameof(CanCancel))]
    protected virtual void OnCancel()
    {
        //
        RequestWindowClose = true;
    }

    IRelayCommand? _WindowClosingCommand;
    public IRelayCommand WindowClosingCommand => _WindowClosingCommand ??= new CommunityToolkit.Mvvm.Input.RelayCommand(OnWindowClosing, CanClose);
    protected virtual bool CanClose() => !IsWorking;
    //[RelayCommand(CanExecute = nameof(CanClose))]
    protected virtual void OnWindowClosing()
    {

    }

    #endregion

}