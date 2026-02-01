@echo off
setlocal
set DIR=%~dp0
set JAVA_EXE=java
if defined JAVA_HOME set JAVA_EXE="%JAVA_HOME%\bin\java"
%JAVA_EXE% -Xmx64m -Xms64m -classpath "%DIR%gradle\wrapper\gradle-wrapper.jar" org.gradle.wrapper.GradleWrapperMain %*
endlocal