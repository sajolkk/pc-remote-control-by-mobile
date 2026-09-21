allprojects {
    repositories {
        google()
        mavenCentral()
    }
}

// Pin the NDK for every Android subproject, not just :app.
//
// Each Flutter plugin's build file sets ndkVersion = flutter.ndkVersion, so pinning only the app
// module leaves plugin modules demanding whichever NDK this Flutter release prefers. When that one
// is absent the build fails, even though nothing here compiles native code. The transitive `jni`
// package is what actually triggers it.
//
// Two ordering constraints, both learned the hard way:
//   * it must be afterEvaluate, because a plugin sets its own ndkVersion during its evaluation and
//     an earlier assignment is simply overwritten;
//   * this block must appear BEFORE the evaluationDependsOn(":app") block below, because that one
//     forces subprojects to evaluate — after which registering an afterEvaluate hook throws
//     "Cannot run Project.afterEvaluate(Action) when the project is already evaluated".
val pinnedNdkVersion = "25.1.8937393"

subprojects {
    afterEvaluate {
        extensions.findByName("android")?.let { android ->
            when (android) {
                is com.android.build.gradle.LibraryExtension -> android.ndkVersion = pinnedNdkVersion
                is com.android.build.gradle.BaseExtension -> android.ndkVersion = pinnedNdkVersion
                else -> {}
            }
        }
    }
}

val newBuildDir: Directory =
    rootProject.layout.buildDirectory
        .dir("../../build")
        .get()
rootProject.layout.buildDirectory.value(newBuildDir)

subprojects {
    val newSubprojectBuildDir: Directory = newBuildDir.dir(project.name)
    project.layout.buildDirectory.value(newSubprojectBuildDir)
}
subprojects {
    project.evaluationDependsOn(":app")
}

tasks.register<Delete>("clean") {
    delete(rootProject.layout.buildDirectory)
}
