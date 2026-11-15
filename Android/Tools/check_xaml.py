"""Structural checks for the MAUI pages: tag balance, and XAML <-> code-behind agreement.

These catch the failure mode that only shows up on a device: a Clicked handler that does not exist, or
a x:Name the code assigns to that the XAML never declares. Both compile fine and explode at runtime.
"""
import re
import sys
from pathlib import Path

root = Path(__file__).resolve().parent.parent
pages = root / "Pages"

PAIRS = [
    ("Pages/ChatPage.xaml", "Pages/ChatPage.xaml.cs"),
    ("Pages/DashboardPage.xaml", "Pages/DashboardPage.xaml.cs"),
    ("Pages/DevicesPage.xaml", "Pages/DevicesPage.xaml.cs"),
    ("Pages/NotesPage.xaml", "Pages/NotesPage.xaml.cs"),
    ("Pages/SettingsPage.xaml", "Pages/SettingsPage.xaml.cs"),
    ("Pages/TasksPage.xaml", "Pages/TasksPage.xaml.cs"),
    ("Pages/SetupWizardPage.xaml", "Pages/SetupWizardPage.xaml.cs"),
    ("AppShell.xaml", "AppShell.xaml.cs"),
]

CONTAINERS = [
    "Border",
    "VerticalStackLayout",
    "HorizontalStackLayout",
    "Grid",
    "ScrollView",
    "CollectionView",
    "ContentPage",
    "StackLayout",
    "Shell",
    "TabBar",
    "ShellContent",
]

problems = []
checked = 0


def tag_counts(text):
    counts = {}
    for tag in CONTAINERS:
        opens = len(re.findall(r"<" + tag + r"(?=[\s/>])", text))
        self_closing = len(re.findall(r"<" + tag + r"(?=[\s/>])[^>]*/>", text))
        closes = len(re.findall(r"</" + tag + r">", text))
        counts[tag] = (opens - self_closing, closes)
    return counts


for xaml_rel, code_rel in PAIRS:
    xaml_path = root / xaml_rel
    code_path = root / code_rel
    if not xaml_path.exists():
        problems.append(f"missing xaml: {xaml_rel}")
        continue
    if not code_path.exists():
        problems.append(f"missing code-behind: {code_rel}")
        continue

    xaml = xaml_path.read_text(encoding="utf-8")
    code = code_path.read_text(encoding="utf-8")
    name = xaml_path.name

    for tag, (opens, closes) in tag_counts(xaml).items():
        if opens != closes:
            problems.append(f"{name}: <{tag}> unbalanced ({opens} open, {closes} close)")
        checked += 1

    names = set(re.findall(r'x:Name="([A-Za-z0-9_]+)"', xaml))
    handlers = set(re.findall(r'(?:Clicked|Toggled|Tapped|SelectionChanged|TextChanged|Command)="([A-Za-z0-9_]+)"', xaml))

    declared = set(re.findall(r"(?:private|public|protected|internal)\s+(?:async\s+)?(?:void|Task)\s+([A-Za-z0-9_]+)\s*\(", code))

    for handler in sorted(handlers):
        checked += 1
        if handler not in declared:
            problems.append(f"{name}: handler '{handler}' is referenced but not defined in {code_path.name}")

    # Fields the code touches as UI properties (Name.Text, Name.Items.Clear(), ...) must exist as x:Name.
    used = set(re.findall(
        r"\b([A-Z][A-Za-z0-9_]*)\.(?:Text|TextColor|IsVisible|IsToggled|IsChecked|IsEnabled|ItemsSource|Items|"
        r"SelectedIndex|Progress|Background|ContentTemplate)\b|"
        r"\b([A-Z][A-Za-z0-9_]*)\.(?:Clear|Add)\(",
        code))
    used = {name for pair in used for name in pair if name}
    for identifier in sorted(used):
        checked += 1
        if identifier in declared:
            continue
        if identifier not in names:
            problems.append(f"{name}: code uses '{identifier}' but the XAML declares no such x:Name")

    checked += 1
    if "InitializeComponent()" not in code:
        problems.append(f"{code_path.name}: never calls InitializeComponent()")

shell = (root / "AppShell.xaml").read_text(encoding="utf-8")
tabs = [re.search(r'Title="([^"]+)"', block).group(1)
        for block in re.findall(r"<ShellContent\b[^>]*/>", shell, re.S)
        if re.search(r'Title="([^"]+)"', block)]
checked += 1
if tabs[:1] != ["对话"]:
    problems.append(f"chat must be the first tab, got {tabs}")

routes = re.findall(r'Route="([^"]+)"', shell)
checked += 1
if "tasks" not in routes:
    problems.append("the tasks tab route is missing; the dashboard links to //tasks")

print(f"checked {checked} structural facts across {len(PAIRS)} files")
if problems:
    print()
    for problem in problems:
        print("PROBLEM:", problem)
    sys.exit(1)

print("tabs:", " / ".join(tabs))
print("all structural checks passed")
