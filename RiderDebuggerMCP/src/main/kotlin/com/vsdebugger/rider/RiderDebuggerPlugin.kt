package com.vsdebugger.rider

import com.intellij.openapi.project.Project
import com.intellij.openapi.startup.StartupActivity

class RiderDebuggerPlugin : StartupActivity {
    override fun runActivity(project: Project) {
        RiderDebuggerServerService.getInstance().ensureStarted()
        McpClientRegistration.registerWithCursor()
        McpClientRegistration.registerWithClaudeDesktop()
        McpClientRegistration.registerWithClaudeCode()
        McpClientRegistration.registerWithOpenCode()
    }
}
