Imports System.Drawing.Text
Imports System.Reflection

''' <summary>
''' "About" window in eero's brand (Forest Night palette).
''' Built in code so it needs no designer/resx file.
''' </summary>
Public Class frmAbout
    Inherits Form

    Private Shared ReadOnly ForestNight As Color = ColorTranslator.FromHtml("#1F3025")
    Private Shared ReadOnly Cream As Color = ColorTranslator.FromHtml("#EFE9DD")
    Private Shared ReadOnly Saffron As Color = ColorTranslator.FromHtml("#E2A03B")
    Private Shared ReadOnly Rust As Color = ColorTranslator.FromHtml("#C45A2C")
    Private Shared ReadOnly CreamDim As Color = Color.FromArgb(160, 170, 160)

    Private ReadOnly _fontName As String = PickFontName()

    Public Sub New()
        Text = "About"
        FormBorderStyle = FormBorderStyle.FixedDialog
        MaximizeBox = False
        MinimizeBox = False
        ShowInTaskbar = False
        StartPosition = FormStartPosition.CenterParent
        BackColor = ForestNight
        ForeColor = Cream
        ClientSize = New Size(480, 420)
        Font = New Font(_fontName, 9.75!)

        Try
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath)
        Catch
        End Try

        BuildLayout()
    End Sub

    Private Sub BuildLayout()
        ' --- Header lockup: [eero.] | FRUSTRATIONSFRIT IT -----------------------------
        Dim logo As New PictureBox() With {
            .Location = New Point(28, 24),
            .Size = New Size(150, 50),
            .SizeMode = PictureBoxSizeMode.Zoom,
            .BackColor = Color.Transparent,
            .Image = LoadLogo()
        }
        Controls.Add(logo)

        Dim divider As New Panel() With {
            .Location = New Point(192, 38),
            .Size = New Size(1, 22),
            .BackColor = CreamDim
        }
        Controls.Add(divider)

        Controls.Add(MakeLabel("FRUSTRATIONSFRIT IT", 8.25!, FontStyle.Bold, Cream, New Point(206, 41)))

        ' --- Title ---------------------------------------------------------------------
        Dim title = MakeLabel("RFID/NFC UID Keyboard Emulator", 15.75!, FontStyle.Bold, Saffron, New Point(26, 104))
        Controls.Add(title)
        Dim titleDot = MakeLabel(".", 15.75!, FontStyle.Bold, Rust, Point.Empty)
        Controls.Add(titleDot)
        AddHandler title.SizeChanged, Sub() titleDot.Location = New Point(title.Right - 6, title.Top)
        titleDot.Location = New Point(title.Right - 6, title.Top)

        Dim version = Assembly.GetExecutingAssembly().GetName().Version
        Controls.Add(MakeLabel($"Version {version.Major}.{version.Minor}.{version.Build}", 9.0!, FontStyle.Regular, CreamDim, New Point(28, 138)))

        ' --- Body ----------------------------------------------------------------------
        Dim body = MakeLabel(
            "Udgivet og vedligeholdt af eero." & vbCrLf & vbCrLf &
            "Læser UID'et fra RFID/NFC-kort og skriver det ind som tastatur-input. " &
            "Den fastgjorte læser genforbindes automatisk, hvis forbindelsen mistes.",
            9.75!, FontStyle.Regular, Cream, New Point(28, 170))
        body.AutoSize = False
        body.Size = New Size(424, 80)
        Controls.Add(body)

        ' --- Contact -------------------------------------------------------------------
        Controls.Add(MakeLabel("BRUG FOR HJÆLP?", 8.25!, FontStyle.Bold, Saffron, New Point(28, 258)))
        Controls.Add(MakeLink("eero.dk", "https://eero.dk", New Point(28, 280)))
        Controls.Add(MakeLink("support@eero.dk", "mailto:support@eero.dk", New Point(120, 280)))
        Controls.Add(MakeLabel("+45 70 30 70 60", 9.75!, FontStyle.Regular, Cream, New Point(280, 280)))

        ' --- Credit for the original project (required by the BSD 2-Clause license) ---
        Dim credit = MakeLabel(
            "Baseret på NFC-UID-Keyboard-Emulator af Thanayut T. og mkapulica. " &
            "Bruger PC/SC-sharp af Daniel Mueller. Udgivet under BSD 2-Clause-licensen.",
            8.25!, FontStyle.Regular, CreamDim, New Point(28, 318))
        credit.AutoSize = False
        credit.Size = New Size(424, 36)
        Controls.Add(credit)

        ' --- Close button (rust CTA) ----------------------------------------------------
        Dim btnClose As New Button() With {
            .Text = "Luk",
            .Size = New Size(100, 32),
            .Location = New Point(ClientSize.Width - 128, ClientSize.Height - 50),
            .FlatStyle = FlatStyle.Flat,
            .BackColor = Rust,
            .ForeColor = Cream,
            .Font = New Font(_fontName, 9.75!, FontStyle.Bold),
            .DialogResult = DialogResult.OK
        }
        btnClose.FlatAppearance.BorderSize = 0
        Controls.Add(btnClose)
        AcceptButton = btnClose
        CancelButton = btnClose
    End Sub

    Private Function MakeLabel(text As String, size As Single, style As FontStyle, color As Color, location As Point) As Label
        Return New Label() With {
            .Text = text,
            .AutoSize = True,
            .Font = New Font(_fontName, size, style),
            .ForeColor = color,
            .BackColor = Color.Transparent,
            .Location = location
        }
    End Function

    Private Function MakeLink(text As String, url As String, location As Point) As LinkLabel
        Dim link As New LinkLabel() With {
            .Text = text,
            .AutoSize = True,
            .Font = New Font(_fontName, 9.75!),
            .LinkColor = Saffron,
            .ActiveLinkColor = Rust,
            .VisitedLinkColor = Saffron,
            .BackColor = Color.Transparent,
            .Location = location
        }
        AddHandler link.LinkClicked, Sub()
                                         Try
                                             Process.Start(url)
                                         Catch
                                         End Try
                                     End Sub
        Return link
    End Function

    Private Shared Function LoadLogo() As Image
        Try
            Dim asm = Assembly.GetExecutingAssembly()
            Dim name = asm.GetManifestResourceNames().FirstOrDefault(Function(n) n.EndsWith("eero_logo_white.png", StringComparison.OrdinalIgnoreCase))
            If name Is Nothing Then Return Nothing
            Using s = asm.GetManifestResourceStream(name)
                Return New Bitmap(Image.FromStream(s))
            End Using
        Catch
            Return Nothing
        End Try
    End Function

    ' eero's brand font is Miriam Libre; fall back to Segoe UI if it isn't installed.
    Private Shared Function PickFontName() As String
        Using fonts As New InstalledFontCollection()
            If fonts.Families.Any(Function(f) f.Name = "Miriam Libre") Then Return "Miriam Libre"
        End Using
        Return "Segoe UI"
    End Function

End Class
