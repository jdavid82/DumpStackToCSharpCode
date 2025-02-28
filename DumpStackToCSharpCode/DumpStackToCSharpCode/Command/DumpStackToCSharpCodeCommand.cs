﻿using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using DumpStackToCSharpCode.StackFrameAnalyzer;
using DumpStackToCSharpCode.Window;
using System;
using System.ComponentModel.Design;
using DumpStackToCSharpCode.Options;
using Task = System.Threading.Tasks.Task;
using DumpStackToCSharpCode.CurrentStack;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using DumpStackToCSharpCode.ObjectInitializationGeneration.Constructor;
using static DumpStackToCSharpCode.Options.DialogPageProvider;
using DumpStackToCSharpCode.Command.Util;

namespace DumpStackToCSharpCode.Command
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class DumpStackToCSharpCodeCommand
    {
        /// <summary>
        /// Command ID.
        /// </summary>
        public const int CommandId = 0x0100;

        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CommandSet = new Guid("546abd90-d54f-42c1-a8ac-26fdd0f6447d");

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly AsyncPackage package;

        private static DTE2 _dte;
        private static DebuggerEvents _debuggerEvents;
        private ICurrentStackWrapper _currentStackWrapper;
        private ArgumentsListPurifier _argumentsListPurifier;

        /// <summary>
        /// Initializes a new instance of the <see cref="DumpStackToCSharpCodeCommand"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        /// <param name="commandService">Command service to add command to, not null.</param>
        private DumpStackToCSharpCodeCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
            _currentStackWrapper = new CurrentStackWrapper();
            _argumentsListPurifier = new ArgumentsListPurifier();
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static DumpStackToCSharpCodeCommand Instance
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the service provider from the owner package.
        /// </summary>
        private StackDataDumpControl _stackDataDumpControl;

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            // Switch to the main thread - the call to AddCommand in DumpStackToCSharpCodeCommand's constructor requires
            // the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var dte = await package.GetServiceAsync(typeof(DTE)) ?? throw new Exception("GetServiceAsync returned DTE null");

            _dte = dte as DTE2;
            _debuggerEvents = _dte.Events.DebuggerEvents;

            var commandService = await package.GetServiceAsync((typeof(IMenuCommandService))) as OleMenuCommandService;
            Instance = new DumpStackToCSharpCodeCommand(package, commandService);
        }
        public void SubscribeForReadOnlyObjectArgumentsPageProviderEvents(EventHandler<bool> action, EventHandler<bool> onReadOnlyObjectArgumentsOptionsSave)
        {
            var pageProvider = package.GetDialogPage(typeof(DialogPageProvider.ReadOnlyObjectArgumentsPageProvider)) as ReadOnlyObjectArgumentsPageProvider;
            pageProvider.OnSettingsPageActivate += action;
            pageProvider.SubstribeForModelSave(onReadOnlyObjectArgumentsOptionsSave);
        }

        public IReadOnlyCollection<CurrentExpressionOnStack> GetCurrentStack()
        {
            return _currentStackWrapper.RefreshCurrentLocals(_dte);
        }

        public void SubscribeForDebuggerContextChange()
        {
            _debuggerEvents.OnContextChanged += OnDebuggerContextChange;
        }

        public void UnSubscribeForDebuggerContextChange()
        {
            _debuggerEvents.OnContextChanged -= OnDebuggerContextChange;
        }

        public void SubscribeForDebuggerContextChange(_dispDebuggerEvents_OnContextChangedEventHandler eventHandler)
        {
            _debuggerEvents.OnContextChanged += eventHandler;
        }
        public void UnSubscribeForDebuggerContextChange(_dispDebuggerEvents_OnContextChangedEventHandler eventHandler)
        {
            _debuggerEvents.OnContextChanged -= eventHandler;
        }
        public void ResetCurrentStack()
        {
            _currentStackWrapper.Reset();
        }

        public async Task OnSettingsSaveAsync()
        {
            var generalOptions = await GeneralOptions.GetLiveInstanceAsync();
            if (_stackDataDumpControl == null)
            {
                var window = await package.FindToolWindowAsync(typeof(StackDataDump), 0, true, package.DisposalToken);
                var stackDataDump = window as StackDataDump;
                _stackDataDumpControl = stackDataDump?.Content as StackDataDumpControl;
            }

            _stackDataDumpControl.MaxDepth.Text = generalOptions.MaxObjectDepth.ToString();
            _stackDataDumpControl.AutomaticallyRefresh.IsChecked = generalOptions.AutomaticallyRefresh;
        }

        private async void OnDebuggerContextChange(Process newprocess, Program newprogram, Thread newthread, StackFrame newstackframe)
        {
            try
            {
                await DumpStackToCSharpCodeAsync(new List<string>());
            }
            catch (Exception e)
            {
                _stackDataDumpControl.LogException(e);
            }
        }

        /// <summary>
        /// This function is the callback used to execute the command when the menu item is clicked.
        /// See the constructor to see how the menu item is associated with this function using
        /// OleMenuCommandService service and MenuCommand class.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>


        public async void Execute(object sender, EventArgs e)
        {
            try
            {
                IList<string> locals = new List<string>();
                if (e is ChosenLocalsEventArgs chosenLocals)
                {
                    locals = chosenLocals.CkeckedLocals;
                }
                await DumpStackToCSharpCodeAsync(locals);
            }
            catch (Exception exception)
            {
                _stackDataDumpControl.ResetControls();
                await RefreshUI();
                _stackDataDumpControl.LogException(exception);
            }
        }

        private async Task DumpStackToCSharpCodeAsync(IList<string> chosenLocals)
        {
            if (_stackDataDumpControl == null)
            {
                await package.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var window = await package.FindToolWindowAsync(typeof(StackDataDump), 0, true, package.DisposalToken);
                    var windowFrame = (IVsWindowFrame)window.Frame;
                    if (windowFrame.IsVisible() != 0)
                    {
                        Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(windowFrame.Show());
                    }

                    var stackDataDump = window as StackDataDump;
                    _stackDataDumpControl = stackDataDump?.Content as StackDataDumpControl;
                    _currentStackWrapper.RefreshCurrentLocals(_dte);
                    await DumpStackToCSharpCodeAsync(chosenLocals);
                });
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _stackDataDumpControl.ClearControls();
            await RefreshUI();

            var debuggerStackToDumpedObject = new DebuggerStackToDumpedObject();
            if (_currentStackWrapper.CurrentExpressionOnStacks == null)
            {
                _currentStackWrapper.RefreshCurrentLocals(_dte);
            }

            if (_currentStackWrapper.CurrentExpressionOnStacks == null)
            {
                _stackDataDumpControl.ResetControls();
                return;
            }

            var locals = _currentStackWrapper.CurrentExpressionOnStacks
                .Where(x => chosenLocals.Count == 0 || chosenLocals.Any(y => y == x.Name))
                .Select(x => x.Expression).ToList();
            var readonlyObjects = GetConstructorArguments();

            var dumpedObjectsToCsharpCode = debuggerStackToDumpedObject.DumpObjectOnStack(locals,
                                                                                          int.Parse(_stackDataDumpControl.MaxDepth.Text),
                                                                                          GeneralOptions.Instance.GenerateTypeWithNamespace,
                                                                                          GeneralOptions.Instance.MaxObjectsToAnalyze,
                                                                                          GeneralOptions.Instance.MaxGenerationTime,
                                                                                          readonlyObjects,
                                                                                          GeneralOptions.Instance.GenerateConcreteType);

            _stackDataDumpControl.CreateStackDumpControls(dumpedObjectsToCsharpCode.dumpedObjectToCsharpCode, dumpedObjectsToCsharpCode.errorMessage);
        }

        private Dictionary<string, IReadOnlyList<string>> GetConstructorArguments()
        {
            var readonlyObjects = new Dictionary<string, IReadOnlyList<string>>(new OrdinalIgnoreCaseComparer());

            for (int i = 0; i < _stackDataDumpControl.Class.Children.Count; i++)
            {
                var classNameTextBox = _stackDataDumpControl.Class.Children[i] as TextBox;

                var className = classNameTextBox.Text.Trim();

                if (string.IsNullOrWhiteSpace(className))
                {
                    continue;
                }

                var argumentListTextBox = _stackDataDumpControl.Arguments.Children[i] as TextBox;
                var argumentList = argumentListTextBox.Text.Trim().Split(new[] { ",", ";" }, StringSplitOptions.RemoveEmptyEntries).ToList();

                if (argumentList.Count == 0)
                {
                    continue;
                }

                var purifiedArgumentList = _argumentsListPurifier.Purify(argumentList);
                readonlyObjects[className] = purifiedArgumentList;
            }

            return readonlyObjects;
        }

        private async Task RefreshUI()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                var vsUiShell = (IVsUIShell)await package.GetServiceAsync(typeof(IVsUIShell));
                if (vsUiShell != null)
                {
                    int hr = vsUiShell.UpdateCommandUI(0);
                    Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(hr);
                }
            }
            catch (Exception e)
            {
                _stackDataDumpControl.LogException(e);
            }
        }
    }
}
