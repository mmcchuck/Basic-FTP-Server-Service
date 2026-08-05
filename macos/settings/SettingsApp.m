#import <Cocoa/Cocoa.h>

static NSString *const CliPath = @"/Library/Application Support/Basic FTP Server Service/app/basic-ftp-server";
static NSString *const ServiceLabel = @"system/com.basicftpserverservice.daemon";

@interface AppDelegate : NSObject <NSApplicationDelegate, NSTableViewDataSource, NSTableViewDelegate>
@property NSWindow *window;
@property NSTableView *table;
@property NSMutableArray<NSDictionary *> *users;
@property NSTextField *nameField;
@property NSSecureTextField *passwordField;
@property NSTextField *folderField;
@property NSButton *enabledCheck;
@property NSButton *readCheck;
@property NSButton *deleteCheck;
@property NSTextField *statusLabel;
@property NSButton *saveButton;
@property NSString *editingName;
@end

@implementation AppDelegate

- (void)applicationDidFinishLaunching:(NSNotification *)notification {
    self.users = [NSMutableArray array];
    [self buildWindow];
    [self.window makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];
    [self refresh:nil];
}

- (BOOL)applicationShouldTerminateAfterLastWindowClosed:(NSApplication *)sender { return YES; }

- (NSButton *)button:(NSString *)title action:(SEL)action {
    NSButton *button = [NSButton buttonWithTitle:title target:self action:action];
    button.bezelStyle = NSBezelStyleRounded;
    return button;
}

- (NSTextField *)label:(NSString *)text size:(CGFloat)size weight:(NSFontWeight)weight {
    NSTextField *label = [NSTextField labelWithString:text];
    label.font = [NSFont systemFontOfSize:size weight:weight];
    return label;
}

- (void)buildWindow {
    self.window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0, 0, 840, 520)
        styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable |
                  NSWindowStyleMaskMiniaturizable | NSWindowStyleMaskResizable
        backing:NSBackingStoreBuffered defer:NO];
    self.window.title = @"Basic FTP Server Settings";
    self.window.minSize = NSMakeSize(780, 480);
    [self.window center];

    NSStackView *root = [NSStackView stackViewWithViews:@[]];
    root.orientation = NSUserInterfaceLayoutOrientationVertical;
    root.spacing = 12;
    root.edgeInsets = NSEdgeInsetsMake(18, 18, 18, 18);
    root.translatesAutoresizingMaskIntoConstraints = NO;
    [self.window.contentView addSubview:root];
    [NSLayoutConstraint activateConstraints:@[
        [root.leadingAnchor constraintEqualToAnchor:self.window.contentView.leadingAnchor],
        [root.trailingAnchor constraintEqualToAnchor:self.window.contentView.trailingAnchor],
        [root.topAnchor constraintEqualToAnchor:self.window.contentView.topAnchor],
        [root.bottomAnchor constraintEqualToAnchor:self.window.contentView.bottomAnchor]
    ]];

    [root addArrangedSubview:[self label:@"FTP Users" size:22 weight:NSFontWeightSemibold]];
    NSStackView *body = [NSStackView stackViewWithViews:@[]];
    body.orientation = NSUserInterfaceLayoutOrientationHorizontal;
    body.spacing = 18;
    body.distribution = NSStackViewDistributionFill;
    [root addArrangedSubview:body];

    NSStackView *left = [NSStackView stackViewWithViews:@[]];
    left.orientation = NSUserInterfaceLayoutOrientationVertical;
    left.spacing = 8;
    [left.widthAnchor constraintEqualToConstant:370].active = YES;
    [body addArrangedSubview:left];

    self.table = [[NSTableView alloc] init];
    NSArray *columns = @[@[@"name", @"Username", @110], @[@"folder", @"Scan Folder", @195], @[@"state", @"State", @60]];
    for (NSArray *spec in columns) {
        NSTableColumn *column = [[NSTableColumn alloc] initWithIdentifier:spec[0]];
        column.title = spec[1];
        column.width = [spec[2] doubleValue];
        [self.table addTableColumn:column];
    }
    self.table.delegate = self;
    self.table.dataSource = self;
    self.table.headerView = [[NSTableHeaderView alloc] init];
    self.table.usesAlternatingRowBackgroundColors = YES;
    self.table.target = self;
    self.table.doubleAction = @selector(editSelected:);
    NSScrollView *scroll = [[NSScrollView alloc] init];
    scroll.documentView = self.table;
    scroll.hasVerticalScroller = YES;
    scroll.borderType = NSBezelBorder;
    [scroll.heightAnchor constraintGreaterThanOrEqualToConstant:320].active = YES;
    [left addArrangedSubview:scroll];

    NSStackView *listButtons = [NSStackView stackViewWithViews:@[
        [self button:@"New" action:@selector(newUser:)],
        [self button:@"Edit" action:@selector(editSelected:)],
        [self button:@"Remove" action:@selector(removeSelected:)],
        [self button:@"Refresh" action:@selector(refresh:)]
    ]];
    listButtons.orientation = NSUserInterfaceLayoutOrientationHorizontal;
    listButtons.spacing = 8;
    [left addArrangedSubview:listButtons];

    NSStackView *form = [NSStackView stackViewWithViews:@[]];
    form.orientation = NSUserInterfaceLayoutOrientationVertical;
    form.spacing = 9;
    form.alignment = NSLayoutAttributeLeading;
    [body addArrangedSubview:form];
    [form addArrangedSubview:[self label:@"Account" size:14 weight:NSFontWeightSemibold]];
    [form addArrangedSubview:[NSTextField labelWithString:@"Username"]];
    self.nameField = [[NSTextField alloc] init];
    [self.nameField.widthAnchor constraintGreaterThanOrEqualToConstant:350].active = YES;
    [form addArrangedSubview:self.nameField];
    [form addArrangedSubview:[NSTextField labelWithString:@"Password"]];
    self.passwordField = [[NSSecureTextField alloc] init];
    self.passwordField.placeholderString = @"Required for new users; blank keeps current password";
    [self.passwordField.widthAnchor constraintGreaterThanOrEqualToConstant:350].active = YES;
    [form addArrangedSubview:self.passwordField];
    [form addArrangedSubview:[NSTextField labelWithString:@"Scan folder"]];

    self.folderField = [[NSTextField alloc] init];
    self.folderField.placeholderString = @"/Users/Shared/Scans";
    [self.folderField.widthAnchor constraintGreaterThanOrEqualToConstant:270].active = YES;
    NSStackView *folderRow = [NSStackView stackViewWithViews:@[
        self.folderField, [self button:@"Choose…" action:@selector(chooseFolder:)]
    ]];
    folderRow.orientation = NSUserInterfaceLayoutOrientationHorizontal;
    folderRow.spacing = 6;
    [form addArrangedSubview:folderRow];

    [form addArrangedSubview:[self label:@"Permissions" size:14 weight:NSFontWeightSemibold]];
    self.enabledCheck = [NSButton checkboxWithTitle:@"Account enabled" target:nil action:nil];
    self.readCheck = [NSButton checkboxWithTitle:@"Allow downloads" target:nil action:nil];
    self.deleteCheck = [NSButton checkboxWithTitle:@"Allow deleting files" target:nil action:nil];
    [form addArrangedSubview:self.enabledCheck];
    [form addArrangedSubview:self.readCheck];
    [form addArrangedSubview:self.deleteCheck];
    NSTextField *note = [NSTextField wrappingLabelWithString:@"Uploads, folder creation, and directory listing are always enabled. Downloads and deletion are off by default."];
    note.textColor = NSColor.secondaryLabelColor;
    note.font = [NSFont systemFontOfSize:11];
    note.maximumNumberOfLines = 3;
    [note.widthAnchor constraintEqualToConstant:350].active = YES;
    [form addArrangedSubview:note];

    self.saveButton = [self button:@"Add User" action:@selector(saveUser:)];
    self.saveButton.keyEquivalent = @"\r";
    [form addArrangedSubview:self.saveButton];

    NSStackView *footer = [NSStackView stackViewWithViews:@[]];
    footer.orientation = NSUserInterfaceLayoutOrientationHorizontal;
    footer.spacing = 10;
    self.statusLabel = [NSTextField labelWithString:@"Checking service…"];
    self.statusLabel.textColor = NSColor.secondaryLabelColor;
    [footer addArrangedSubview:self.statusLabel];
    [footer addArrangedSubview:[[NSView alloc] init]];
    [footer addArrangedSubview:[self button:@"Restart Service" action:@selector(restartService:)]];
    [root addArrangedSubview:footer];
    [self newUser:nil];
}

- (NSInteger)numberOfRowsInTableView:(NSTableView *)tableView { return self.users.count; }

- (NSView *)tableView:(NSTableView *)tableView viewForTableColumn:(NSTableColumn *)column row:(NSInteger)row {
    NSDictionary *user = self.users[row];
    NSString *identifier = column.identifier;
    NSString *value = [identifier isEqualToString:@"name"] ? user[@"name"] :
        ([identifier isEqualToString:@"folder"] ? user[@"homeDirectory"] :
         ([user[@"enabled"] boolValue] ? @"Enabled" : @"Disabled"));
    return [NSTextField labelWithString:value ?: @""];
}

- (void)refresh:(id)sender {
    NSError *error = nil;
    NSString *output = [self privileged:[NSString stringWithFormat:@"%@ list-users --json", [self shellQuote:CliPath]] error:&error];
    if (!output) { [self showError:error]; return; }
    NSData *data = [output dataUsingEncoding:NSUTF8StringEncoding];
    NSArray *decoded = [NSJSONSerialization JSONObjectWithData:data options:0 error:&error];
    if (!decoded) { [self showError:error]; return; }
    self.users = [decoded mutableCopy];
    [self.table reloadData];
    NSUInteger enabled = [self.users filteredArrayUsingPredicate:[NSPredicate predicateWithFormat:@"enabled == YES"]].count;
    self.statusLabel.stringValue = [NSString stringWithFormat:@"Service installed • %lu enabled user(s)", (unsigned long)enabled];
}

- (void)newUser:(id)sender {
    self.editingName = nil;
    self.nameField.enabled = YES;
    self.nameField.stringValue = @"";
    self.passwordField.stringValue = @"";
    self.folderField.stringValue = @"/Users/Shared/Scans";
    self.enabledCheck.state = NSControlStateValueOn;
    self.readCheck.state = NSControlStateValueOff;
    self.deleteCheck.state = NSControlStateValueOff;
    self.saveButton.title = @"Add User";
}

- (void)editSelected:(id)sender {
    if (self.table.selectedRow < 0) return;
    NSDictionary *user = self.users[self.table.selectedRow];
    self.editingName = user[@"name"];
    self.nameField.stringValue = user[@"name"];
    self.nameField.enabled = NO;
    self.passwordField.stringValue = @"";
    self.folderField.stringValue = user[@"homeDirectory"];
    self.enabledCheck.state = [user[@"enabled"] boolValue] ? NSControlStateValueOn : NSControlStateValueOff;
    self.readCheck.state = [user[@"read"] boolValue] ? NSControlStateValueOn : NSControlStateValueOff;
    self.deleteCheck.state = [user[@"delete"] boolValue] ? NSControlStateValueOn : NSControlStateValueOff;
    self.saveButton.title = @"Save Changes";
}

- (void)chooseFolder:(id)sender {
    NSOpenPanel *panel = [NSOpenPanel openPanel];
    panel.canChooseDirectories = YES;
    panel.canChooseFiles = NO;
    panel.canCreateDirectories = YES;
    panel.allowsMultipleSelection = NO;
    panel.prompt = @"Choose Scan Folder";
    if ([panel runModal] == NSModalResponseOK) self.folderField.stringValue = panel.URL.path;
}

- (void)saveUser:(id)sender {
    NSString *name = [self.nameField.stringValue stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
    NSString *folder = [self.folderField.stringValue stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
    NSString *password = self.passwordField.stringValue;
    if (!name.length || !folder.length || (!self.editingName && !password.length)) {
        [self showMessage:@"Enter a username, password, and scan folder. Existing users may leave the password blank to keep it unchanged."];
        return;
    }
    NSMutableString *command = [NSMutableString stringWithFormat:@"%@ set-user %@ %@ %@",
        [self shellQuote:CliPath], [self shellQuote:name], [self shellQuote:password], [self shellQuote:folder]];
    if (self.readCheck.state == NSControlStateValueOn) [command appendString:@" --read"];
    if (self.deleteCheck.state == NSControlStateValueOn) [command appendString:@" --delete"];
    if (self.enabledCheck.state != NSControlStateValueOn) [command appendString:@" --disabled"];
    [command appendFormat:@" && /bin/launchctl kickstart -k %@", ServiceLabel];
    NSError *error = nil;
    if (![self privileged:command error:&error]) { [self showError:error]; return; }
    [self refresh:nil];
    [self showMessage:self.editingName ? @"FTP user updated." : @"FTP user added."];
    [self newUser:nil];
}

- (void)removeSelected:(id)sender {
    if (self.table.selectedRow < 0) return;
    NSDictionary *user = self.users[self.table.selectedRow];
    NSAlert *alert = [[NSAlert alloc] init];
    alert.messageText = [NSString stringWithFormat:@"Remove %@?", user[@"name"]];
    alert.informativeText = [NSString stringWithFormat:@"The FTP account will be removed. Files in %@ will not be deleted.", user[@"homeDirectory"]];
    [alert addButtonWithTitle:@"Remove"];
    [alert addButtonWithTitle:@"Cancel"];
    if ([alert runModal] != NSAlertFirstButtonReturn) return;
    NSString *command = [NSString stringWithFormat:@"%@ remove-user %@ && /bin/launchctl kickstart -k %@",
        [self shellQuote:CliPath], [self shellQuote:user[@"name"]], ServiceLabel];
    NSError *error = nil;
    if (![self privileged:command error:&error]) { [self showError:error]; return; }
    [self refresh:nil];
    [self newUser:nil];
}

- (void)restartService:(id)sender {
    NSError *error = nil;
    if (![self privileged:[NSString stringWithFormat:@"/bin/launchctl kickstart -k %@", ServiceLabel] error:&error]) {
        [self showError:error]; return;
    }
    self.statusLabel.stringValue = @"Service restarted successfully";
}

- (NSString *)privileged:(NSString *)command error:(NSError **)error {
    NSString *escaped = [[command stringByReplacingOccurrencesOfString:@"\\" withString:@"\\\\"]
                         stringByReplacingOccurrencesOfString:@"\"" withString:@"\\\""];
    NSAppleScript *script = [[NSAppleScript alloc] initWithSource:
        [NSString stringWithFormat:@"do shell script \"%@\" with administrator privileges", escaped]];
    NSDictionary *errorInfo = nil;
    NSAppleEventDescriptor *result = [script executeAndReturnError:&errorInfo];
    if (!result) {
        if (error) *error = [NSError errorWithDomain:@"BasicFtpServerSettings" code:1
            userInfo:@{NSLocalizedDescriptionKey: errorInfo[NSAppleScriptErrorMessage] ?: @"Administrator command failed."}];
        return nil;
    }
    return result.stringValue ?: @"";
}

- (NSString *)shellQuote:(NSString *)value {
    return [NSString stringWithFormat:@"'%@'", [value stringByReplacingOccurrencesOfString:@"'" withString:@"'\\''"]];
}

- (void)showMessage:(NSString *)message {
    NSAlert *alert = [[NSAlert alloc] init];
    alert.messageText = message;
    [alert runModal];
}

- (void)showError:(NSError *)error {
    if (error) [[NSAlert alertWithError:error] runModal];
}

@end

int main(int argc, const char *argv[]) {
    @autoreleasepool {
        NSApplication *app = NSApplication.sharedApplication;
        AppDelegate *delegate = [[AppDelegate alloc] init];
        app.delegate = delegate;
        [app setActivationPolicy:NSApplicationActivationPolicyRegular];
        [app run];
    }
    return 0;
}
