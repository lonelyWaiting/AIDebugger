package com.vsdebugger.rider

import com.intellij.openapi.Disposable
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.Service
import com.intellij.openapi.diagnostic.Logger

@Service(Service.Level.APP)
class RiderDebuggerServerService : Disposable {
    private val logger = Logger.getInstance(RiderDebuggerServerService::class.java)
    private val debuggerService = DebuggerService()
    private val server = NamedPipeServer(debuggerService) { msg -> logger.info(msg) }

    fun ensureStarted() {
        server.start()
    }

    override fun dispose() {
        server.stop()
    }

    companion object {
        fun getInstance(): RiderDebuggerServerService {
            return ApplicationManager.getApplication().getService(RiderDebuggerServerService::class.java)
        }
    }
}
