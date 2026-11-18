Imports System.IO
Imports System.Text.RegularExpressions
Imports PCSC
Imports PCSC.Iso7816
Imports PCSC.Monitoring

Public Class frmMain

    Private Shared ReadOnly _contextFactory As IContextFactory = ContextFactory.Instance
    Private _settingsManager As SettingsManager

    ' Name of the reader the running monitor is attached to (the actual PC/SC name).
    Private readerName As String
    ' Name of the reader we want to monitor. When a reader is pinned this is the pinned name.
    Private _targetReader As String

    ' True while the user wants monitoring to be active (Start pressed / auto-start).
    ' The monitor itself may be temporarily down while we wait for the reader to come back.
    Dim isStart As Boolean = False
    Private _monitor As ISCardMonitor
    Private _monitorFailed As Boolean = False   ' Set from the monitor thread when it reports an error.
    Private _isCheckingConnection As Boolean = False
    Private _suppressReaderChange As Boolean = False
    Private _suppressPinChange As Boolean = False

    ' Polls for the reader so we can reconnect after it has been unplugged / lost.
    Private WithEvents _reconnectTimer As New System.Windows.Forms.Timer() With {.Interval = 2000}

    Dim isRunAtStartupEnabled As Boolean = True
    Private _firstShow As Boolean = True

    Public Sub New()
        ' This call is required by the designer.
        InitializeComponent()

        InitializeSettings()
        SetControlsFromSettings()
        LoadReaderList(silent:=IsReaderPinned())
        SelectReaderInList(_settingsManager.Settings.PinnedReaderName)
        AutoStartMonitor()
    End Sub

    Protected Overrides Sub SetVisibleCore(ByVal value As Boolean)
        ' If this is the first time the form is being shown and the Minimize-to-Tray setting is enabled,
        ' force the form to start hidden.
        If _firstShow AndAlso _settingsManager IsNot Nothing AndAlso _settingsManager.Settings.IsMinimizeToTrayEnabled Then
            value = False
        End If

        _firstShow = False
        MyBase.SetVisibleCore(value)
    End Sub

    Private Sub InitializeSettings()
        Dim settingsFilePath As String = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json")
        _settingsManager = New SettingsManager(settingsFilePath)
        ManageStartupShortcut(_settingsManager.Settings.IsRunAtStartupEnabled)
    End Sub

    Private Sub AutoStartMonitor()
        ' A pinned reader is always monitored automatically - that is the point of pinning it.
        If _settingsManager.Settings.IsAutoStartMonitorEnabled OrElse IsReaderPinned() Then
            StartMonitor()
        End If
        UpdateStartButton()
    End Sub

    Private Sub SetControlsFromSettings()
        SetStartupCheckboxes(_settingsManager.Settings.IsRunAtStartupEnabled)
        rbReversed.Checked = _settingsManager.Settings.IsReversedByteOrder
        rbOriginal.Checked = Not _settingsManager.Settings.IsReversedByteOrder
        chkSendEnter.Checked = _settingsManager.Settings.IsSendEnterEnabled
        AutostartMonitorToolStripMenuItem.Checked = _settingsManager.Settings.IsAutoStartMonitorEnabled
        MinimizeToTrayToolStripMenuItem.Checked = _settingsManager.Settings.IsMinimizeToTrayEnabled
        _suppressPinChange = True
        chkPinReader.Checked = IsReaderPinned()
        _suppressPinChange = False
        ManageMinimizeToTrayOption()
        SetStatus("Monitor stopped")
    End Sub

    Private Sub BtnRefreshReader_Click(sender As Object, e As EventArgs) Handles btnRefreshReader.Click
        LoadReaderList(silent:=False)
    End Sub

    Private Sub BtnStartMonitor_Click(sender As Object, e As EventArgs) Handles btnStartMonitor.Click
        ToggleMonitor()
    End Sub

    Private Sub ToggleMonitor()
        If isStart Then
            StopMonitor()
        Else
            StartMonitor()
        End If
        UpdateStartButton()
    End Sub

    Private Sub UpdateStartButton()
        btnStartMonitor.Text = If(isStart, "Stop Monitor", "Start Monitor")
    End Sub

    ' ----------------------------------------------------------------------------------------
    ' Reader list
    ' ----------------------------------------------------------------------------------------

    Private Function GetAvailableReaders() As String()
        Try
            Using context = _contextFactory.Establish(SCardScope.System)
                Dim readers = context.GetReaders()
                If readers Is Nothing Then Return New String() {}
                Return readers
            End Using
        Catch
            ' NoReadersAvailable, ServiceStopped, NoService ... all mean "no reader right now".
            Return New String() {}
        End Try
    End Function

    Function LoadReaderList(Optional silent As Boolean = False) As Boolean
        Dim readerList = GetAvailableReaders()
        Dim previous As String = TryCast(cbxReaderList.SelectedItem, String)

        _suppressReaderChange = True
        Try
            cbxReaderList.DataSource = Nothing
            If readerList.Length > 0 Then
                cbxReaderList.DataSource = readerList
            End If
        Finally
            _suppressReaderChange = False
        End Try

        ' Keep the reader that is in use (or pinned) selected after a refresh.
        Dim preferred = If(readerName, If(_targetReader, previous))
        SelectReaderInList(preferred)

        If readerList.Length = 0 AndAlso Not silent Then
            MessageBox.Show("No card reader detected!", "Message", MessageBoxButtons.OK, MessageBoxIcon.Exclamation)
        End If

        Return readerList.Length > 0
    End Function

    Private Sub SelectReaderInList(name As String)
        If String.IsNullOrEmpty(name) OrElse cbxReaderList.DataSource Is Nothing Then Return
        Dim items = TryCast(cbxReaderList.DataSource, String())
        If items Is Nothing Then Return
        Dim match = ResolveReader(name, items)
        If match Is Nothing Then Return

        _suppressReaderChange = True
        Try
            cbxReaderList.SelectedItem = match
        Finally
            _suppressReaderChange = False
        End Try
    End Sub

    ''' <summary>
    ''' Finds the reader matching <paramref name="wanted"/>. Windows appends an index to reader
    ''' names (e.g. "ACS ACR122 0"), and that index can change when the reader is re-plugged,
    ''' so if there is no exact match we fall back to matching the name without the index.
    ''' </summary>
    Private Shared Function ResolveReader(wanted As String, available As String()) As String
        If String.IsNullOrEmpty(wanted) OrElse available Is Nothing Then Return Nothing

        For Each r In available
            If String.Equals(r, wanted, StringComparison.OrdinalIgnoreCase) Then Return r
        Next

        Dim wantedBase = StripReaderIndex(wanted)
        For Each r In available
            If String.Equals(StripReaderIndex(r), wantedBase, StringComparison.OrdinalIgnoreCase) Then Return r
        Next

        Return Nothing
    End Function

    Private Shared Function StripReaderIndex(name As String) As String
        Return Regex.Replace(name.Trim(), "\s+\d+$", "")
    End Function

    ' ----------------------------------------------------------------------------------------
    ' Monitor lifecycle
    ' ----------------------------------------------------------------------------------------

    Private Function StartMonitor() As Boolean
        Dim target As String = If(IsReaderPinned(), _settingsManager.Settings.PinnedReaderName, TryCast(cbxReaderList.SelectedItem, String))

        If String.IsNullOrEmpty(target) Then
            MessageBox.Show("No card reader detected!" + vbCrLf + "Please try refreshing the reader list.", "Message", MessageBoxButtons.OK, MessageBoxIcon.Exclamation)
            Return False
        End If

        _targetReader = target
        isStart = True
        TryConnect()
        _reconnectTimer.Start()
        Return True
    End Function

    Private Function StopMonitor() As Boolean
        _reconnectTimer.Stop()
        isStart = False
        _targetReader = Nothing
        TearDownMonitor()
        SetStatus("Monitor stopped")
        Return True
    End Function

    ''' <summary>Attaches the PC/SC monitor to the target reader if it is present.</summary>
    Private Function TryConnect() As Boolean
        Dim readers = GetAvailableReaders()
        Dim actual = ResolveReader(_targetReader, readers)

        If actual Is Nothing Then
            SetStatus("Waiting for reader: " & _targetReader)
            Return False
        End If

        Try
            Dim newMonitor = MonitorFactory.Instance.Create(SCardScope.System)
            AddHandler newMonitor.CardInserted, AddressOf CardInit
            AddHandler newMonitor.MonitorException, AddressOf OnMonitorException
            _monitorFailed = False
            _monitor = newMonitor
            newMonitor.Start(actual)
            readerName = actual
        Catch
            TearDownMonitor()
            SetStatus("Waiting for reader: " & _targetReader)
            Return False
        End Try

        ' Reflect the (possibly re-appeared) reader in the drop-down without restarting anything.
        If cbxReaderList.DataSource Is Nothing OrElse Not DirectCast(cbxReaderList.DataSource, String()).Contains(actual) Then
            LoadReaderList(silent:=True)
        Else
            SelectReaderInList(actual)
        End If

        SetStatus("Connected: " & actual)
        Return True
    End Function

    Private Sub TearDownMonitor()
        Dim m = _monitor
        _monitor = Nothing
        readerName = Nothing
        _monitorFailed = False

        If m Is Nothing Then Return
        Try
            RemoveHandler m.CardInserted, AddressOf CardInit
            RemoveHandler m.MonitorException, AddressOf OnMonitorException
            m.Cancel()
        Catch
        End Try
        Try
            m.Dispose()
        Catch
        End Try
    End Sub

    ' Raised on the monitor's background thread, e.g. when the reader is unplugged or the
    ' Smart Card service is stopped. Only set a flag here - the timer does the reconnect.
    Private Sub OnMonitorException(sender As Object, ex As Exceptions.PCSCException)
        _monitorFailed = True
    End Sub

    Private Sub ReconnectTimer_Tick(sender As Object, e As EventArgs) Handles _reconnectTimer.Tick
        If Not isStart OrElse _isCheckingConnection Then Return
        _isCheckingConnection = True
        Try
            If _monitor IsNot Nothing Then
                Dim stillPresent = GetAvailableReaders().Contains(readerName)
                If _monitorFailed OrElse Not _monitor.Monitoring OrElse Not stillPresent Then
                    TearDownMonitor()
                    SetStatus("Reader lost - waiting for: " & _targetReader)
                End If
            End If

            If _monitor Is Nothing Then
                TryConnect()
            End If
        Finally
            _isCheckingConnection = False
        End Try
    End Sub

    Private Sub SetStatus(text As String)
        lblStatus.Text = "Status: " & text
        Dim trayText = "UID Keyboard - " & text
        NotifyIcon1.Text = If(trayText.Length > 63, trayText.Substring(0, 63), trayText)
    End Sub

    ' ----------------------------------------------------------------------------------------
    ' Card handling
    ' ----------------------------------------------------------------------------------------

    Sub CardInit(sender As Object, e As CardStatusEventArgs)
        TypeOutUID(e.ReaderName)
    End Sub

    Function TypeOutUID(cardReaderName As String)
        Try
            Using context = _contextFactory.Establish(SCardScope.System)
                Using rfidReader = context.ConnectReader(cardReaderName, SCardShareMode.Shared, SCardProtocol.Any)
                    Using rfidReader.Transaction(SCardReaderDisposition.Leave)

                        Dim apdu As Byte() = {&HFF, &HCA, &H0, &H0, &H0}
                        Dim sendPci = SCardPCI.GetPci(rfidReader.Protocol)
                        Dim receivePci = New SCardPCI()

                        Dim receiveBuffer = New Byte(255) {}
                        Dim command = apdu.ToArray()
                        Dim bytesReceived = rfidReader.Transmit(sendPci, command, command.Length, receivePci, receiveBuffer, receiveBuffer.Length)
                        Dim responseApdu = New ResponseApdu(receiveBuffer, bytesReceived, IsoCase.Case2Short, rfidReader.Protocol)

                        Dim uidLength = responseApdu.GetData().Length

                        If uidLength <= 0 Then
                            ' Handle case where no UID or unexpected response is received
                            Return False
                        End If

                        Dim uid As Byte() = responseApdu.GetData()

                        If IsReversed() Then
                            Array.Reverse(uid)
                        End If

                        Dim uidString As String = BitConverter.ToString(uid)
                        uidString = uidString.Replace("-", "")

                        If IsSendEnterOptionChecked() Then
                            uidString += "{ENTER}"
                        End If

                        SendKeys.SendWait(uidString)
                    End Using
                End Using
            End Using
        Catch
            'Error Handling should be developed
        End Try

        Return True
    End Function

    Private Function IsReversed()
        Return rbReversed.Checked
    End Function

    Private Function IsSendEnterOptionChecked()
        Return chkSendEnter.Checked
    End Function

    Private Function IsMinimizeToTrayEnabled()
        Return MinimizeToTrayToolStripMenuItem.Checked
    End Function

    Private Function IsReaderPinned() As Boolean
        Return Not String.IsNullOrEmpty(_settingsManager.Settings.PinnedReaderName)
    End Function

    ' ----------------------------------------------------------------------------------------
    ' Settings / UI events
    ' ----------------------------------------------------------------------------------------

    Sub ManageStartupShortcut(runAtStartupEnabled As Boolean)
        Dim shortcutManager As New ShortcutManager()
        If runAtStartupEnabled Then
            shortcutManager.EnsureStartupShortcut()
            shortcutManager.RemoveObsoleteShortcuts()
        Else
            shortcutManager.RemoveStartupShortcut()
        End If
    End Sub

    Private Sub ToggleRunAtStartup()
        _settingsManager.Settings.IsRunAtStartupEnabled = Not _settingsManager.Settings.IsRunAtStartupEnabled
        ManageStartupShortcut(_settingsManager.Settings.IsRunAtStartupEnabled)
        SetStartupCheckboxes(_settingsManager.Settings.IsRunAtStartupEnabled)
    End Sub

    Private Sub SetStartupCheckboxes(checked As Boolean)
        StartupToolStripMenuItem.Checked = checked
        StartupTrayToolStripMenuItem.Checked = checked
    End Sub

    Sub ManageMinimizeToTrayOption()
        If IsMinimizeToTrayEnabled() Then
            NotifyIcon1.Visible = True
            Me.WindowState = FormWindowState.Normal
        Else
            NotifyIcon1.Visible = False
        End If
    End Sub

    Private Sub CbxReaderList_SelectedIndexChanged(sender As Object, e As EventArgs) Handles cbxReaderList.SelectedIndexChanged
        ' Ignore changes made by code (refresh / reconnect).
        If _suppressReaderChange OrElse cbxReaderList.DataSource Is Nothing Then
            Return
        End If

        Dim selected = TryCast(cbxReaderList.SelectedItem, String)
        If String.IsNullOrEmpty(selected) Then Return

        ' Picking another reader while pinned moves the pin to that reader.
        If IsReaderPinned() Then
            _settingsManager.Settings.PinnedReaderName = selected
        End If

        If isStart Then
            _targetReader = selected
            TearDownMonitor()
            TryConnect()
        End If
    End Sub

    Private Sub chkPinReader_CheckedChanged(sender As Object, e As EventArgs) Handles chkPinReader.CheckedChanged
        If _suppressPinChange Then Return

        If chkPinReader.Checked Then
            Dim selected = If(_targetReader, TryCast(cbxReaderList.SelectedItem, String))
            If String.IsNullOrEmpty(selected) Then
                MessageBox.Show("Select a reader first, then pin it.", "Message", MessageBoxButtons.OK, MessageBoxIcon.Exclamation)
                _suppressPinChange = True
                chkPinReader.Checked = False
                _suppressPinChange = False
                Return
            End If
            _settingsManager.Settings.PinnedReaderName = selected

            ' Pinning means "always keep this reader monitored".
            If Not isStart Then
                StartMonitor()
                UpdateStartButton()
            End If
        Else
            _settingsManager.Settings.PinnedReaderName = ""
        End If
    End Sub

    Private Sub frmMain_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        _reconnectTimer.Stop()
        TearDownMonitor()
    End Sub

    Private Sub StartupToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles StartupToolStripMenuItem.Click
        ToggleRunAtStartup()
    End Sub

    ' Handle form resize event to detect minimization
    Private Sub MainForm_Resize(sender As Object, e As EventArgs) Handles MyBase.Resize
        If IsMinimizeToTrayEnabled() AndAlso Me.WindowState = FormWindowState.Minimized Then
            Me.Hide()
        End If
    End Sub

    ' Restore the form when NotifyIcon is double-clicked
    Private Sub NotifyIcon1_MouseDoubleClick(sender As Object, e As MouseEventArgs) Handles NotifyIcon1.MouseDoubleClick
        Me.Show()
        Me.WindowState = FormWindowState.Normal
    End Sub

    ' Context menu item to show the form
    Private Sub ShowToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles ShowToolStripMenuItem.Click
        Me.Show()
        Me.WindowState = FormWindowState.Normal
    End Sub

    ' Context menu item to exit the application
    Private Sub ExitToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles ExitToolStripMenuItem.Click
        Application.Exit()
    End Sub

    Private Sub MinimizeToTrayToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles MinimizeToTrayToolStripMenuItem.Click
        _settingsManager.Settings.IsMinimizeToTrayEnabled = MinimizeToTrayToolStripMenuItem.Checked
        ManageMinimizeToTrayOption()
    End Sub

    Private Sub StartupTrayToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles StartupTrayToolStripMenuItem.Click
        ToggleRunAtStartup()
    End Sub

    Private Sub rbOriginal_CheckedChanged(sender As Object, e As EventArgs) Handles rbOriginal.CheckedChanged
        _settingsManager.Settings.IsReversedByteOrder = Not rbOriginal.Checked
    End Sub

    Private Sub chkSendEnter_CheckedChanged(sender As Object, e As EventArgs) Handles chkSendEnter.CheckedChanged
        _settingsManager.Settings.IsSendEnterEnabled = chkSendEnter.Checked
    End Sub

    Private Sub AboutToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles AboutToolStripMenuItem.Click
        Using about As New frmAbout()
            about.ShowDialog(Me)
        End Using
    End Sub

    Private Sub AutostartMonitorToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles AutostartMonitorToolStripMenuItem.Click
        AutostartMonitorToolStripMenuItem.Checked = Not AutostartMonitorToolStripMenuItem.Checked
        _settingsManager.Settings.IsAutoStartMonitorEnabled = AutostartMonitorToolStripMenuItem.Checked
    End Sub
End Class
