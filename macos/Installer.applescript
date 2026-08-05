use scripting additions

on run
    set appBundle to POSIX path of (path to me)
    set resourcesDir to appBundle & "Contents/Resources/"
    set installerScript to resourcesDir & "install.sh"
    set payloadDir to resourcesDir & "payload"
    set settingsAppName to "Basic FTP Server Settings"

    try
        display dialog ("Basic FTP Server Service will be installed as a background service that starts automatically with your Mac." & return & return & "macOS will ask for an administrator password.") buttons {"Cancel", "Install"} default button "Install" cancel button "Cancel" with title "Basic FTP Server Service"
        do shell script "/bin/bash " & quoted form of installerScript & " " & quoted form of payloadDir with administrator privileges
        display dialog ("Installation finished successfully." & return & return & "Basic FTP Server Settings will now open so you can add scanner accounts.") buttons {"Open Settings"} default button "Open Settings" with title "Basic FTP Server Service" with icon note
        do shell script "/usr/bin/open -a " & quoted form of settingsAppName
    on error errorMessage number errorNumber
        if errorNumber is not -128 then
            display dialog ("Installation failed:" & return & return & errorMessage) buttons {"OK"} default button "OK" with title "Basic FTP Server Service" with icon stop
        end if
    end try
end run
