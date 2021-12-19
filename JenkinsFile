#!groovy
def jobnameparts = JOB_NAME.tokenize('/') as String[]
def solutionFile = "" 
def allSLN = new String[0]
// ***********************************************************************
// <copyright file="JenkinsFile" company="Lectra">
//     Copyright (c) Lectra Systems. All rights reserved.
// </copyright>
// <summary>Build, Test, Package and Archive .Net solution format 2019 - Sdk
// https://docs.microsoft.com/en-us/dotnet/core/porting/
// </summary>
// ***********************************************************************

pipeline {
	parameters {
		choice( choices: ['Release','Debug'],  name: 'BuildConfiguration', description: '.Net build configuration')
		booleanParam(name: 'PublishVersion', defaultValue: false, description: 'Tells whether to publish version of nugets and or setup')
		booleanParam(name: 'RunUnitTest', defaultValue: false, description: 'Tells whether to run unit and integration tests')
    }

	options {
		buildDiscarder(logRotator(numToKeepStr: '10'))
		disableConcurrentBuilds()
		skipDefaultCheckout()
		timeout(time: 1, unit: 'HOURS')
		timestamps()
	}

	agent {label 'msbuild-2019'}

	environment{
		binRepository   = "${BIN_REPOSITORY}"
		artefactsDir    = "${ARTEFACTS_REPOSITORY}"
	}

	stages {

		stage ("Prepare") {
			steps {
				cleanWorkspace();
				// Clone repository
				checkout scm;

				bat """ git fetch --tags --all """
				bat """ git submodule update --init --recursive """
				bat """ dotnet tool install GitVersion.Tool --tool-path .\\distrib --verbosity minimal """; // --version 5.6.4 Remove [Version] argument means latest version
				bat """ .\\distrib\\dotnet-gitversion /output BuildServer /UpdateAssemblyInfo ".\\src\\AssemblyInfo.cs"   /updatewixversionfile """
			
				script {
					readFile('gitversion.properties').split("\r\n").each   { line ->
						el = line.split("=")
						env."${el[0]}" = (el.size() > 1) ? "${el[1]}" : "" 
						}
					currentBuild.displayName = "${GitVersion_FullSemVer}";
					currentBuild.description = "${GitVersion_FullBuildMetaData}";

					//jobnameparts.eachWithIndex { item, idx ->  println(jobnameparts[idx]);}
					
					// Manage solutionFile if build by branch (MultiBranche pipeline)
					def indexJobName = (jobnameparts.length - 1);
					if(jobnameparts[indexJobName].replaceAll('%2F','/') == "${GitVersion_BranchName}"){indexJobName = (indexJobName - 1)}
					// Manage solutionFile if build by tag
					if(jobnameparts[indexJobName].replaceAll('.','').isNumber()){indexJobName = (indexJobName - 1)}

					solutionFile = "${jobnameparts[indexJobName]}";
					binRepository = "${BIN_REPOSITORY}\\${solutionFile}\\${solutionFile.toUpperCase()}";
					artefactsDir= 	"${ARTEFACTS_REPOSITORY}/${solutionFile}";
				}
				println ("SOLUTION 				: ${solutionFile} (${params.BuildConfiguration})")
				println ("BRANCH_NAME			: ${GitVersion_BranchName}");
				println ("SEMANTIC VERSIONING	: ${GitVersion_SemVer}");
				println ("ASSEMBLY FILE VERSION	: ${GitVersion_AssemblySemFileVer}");
				println ("NUGET PCKG VERSION	: ${GitVersion_NuGetVersionV2}");
				println ("Publish Release to	: ${binRepository}");
				println ("Publish Nugets to		: ${artefactsDir}");

			}
		}

		stage ("Build"){
			steps {
				script{
					// RUN THIRDPARTY DEPENDENCIES, IF ANY
					if(fileExists("BuildProcess/scripts/build.xml")){bat """ ant -f ./BuildProcess/scripts/build.xml dependencies """}
					
					// BUILD SOLUTIONS LIST FROM FILE IF PRESENT, 			WARNING : ["".split("\r\n") returns 1 element] in groovy world...
					if(fileExists("BuildProcess.SolutionsBuildOrder.txt")){	
						allSLN = readFile('BuildProcess.SolutionsBuildOrder.txt').split("\r\n");
						if(allSLN[0]==""){allSLN = new String[0]} // WHY 1 =>  ["".split(",")] returns 1 element in groovy world...
						}
					// OR FROM SLN FILE FOUND IN SRC DIRECTORY
					//println("=================================================> ${allSLN.size()} / ${allSLN[0]} <==> ${allSLN.size() <= 1} / ${allSLN[0]==""}")
					
					if(allSLN.size() == 0){allSLN = [".\\src\\${solutionFile}.sln"]} // WHY 1 =>  ["".split(",")] returns 1 element in groovy world...
			        
					allSLN.eachWithIndex{solutionPath, i -> stage(solutionPath) {
							println ("SLN File     			: ${solutionPath}");
							bat """ dotnet clean "${solutionPath}" """
							// RUN BUILD																																						...${WORKSPACE}
							bat """ dotnet build "${solutionPath}" -nodeReuse:false  --configuration "${params.BuildConfiguration}"  /p:Platform="Any CPU" /p:PackageVersion="${GitVersion_NuGetVersionV2}" /p:PackageOutputPath="..\\..\\distrib\\packages"  /flp:"v=diag;logfile=.\\distrib\\Build.txt" """
                        }
                    }
				}
			}
		}

		stage ("Test"){
			when {expression{params.RunUnitTest}}
			steps {
				script{
					allSLN.eachWithIndex{ solutionPath, i -> stage(solutionPath) {
							bat """ dotnet test "${solutionPath}" --settings ".\\src\\Tests.runsettings" --no-build  -l "console;verbosity=detailed" """
						}
					}
				}
			}
		}

		stage("Publish"){
			when {expression{params.PublishVersion && fileExists("distrib/packages")}}
			steps {
				script{

					def nuget = tool name: "nuget-5.0.2.5988", type: "com.cloudbees.jenkins.plugins.customtools.CustomTool"
					withEnv(["PATH+nuget=${nuget}"]){
						for (def file : findFiles(glob: "distrib/packages/*.${GitVersion_NuGetVersionV2}.*nupkg")) { 
						def nugetPath = artefactsDir + '/' + file.name.replaceAll(".${GitVersion_NuGetVersionV2}.*nupkg","");	
						println("Nuget package pushed :" + file.name);
						nugetPush(file.path, nugetPath); 
						}
						currentBuild.displayName = currentBuild.displayName + "+Nugets"		
					
				}
			}
			}
		}

		stage("Setup"){
			when {expression{params.PublishVersion && fileExists("distrib/conf")}}
			steps{
				script {
							def setupVersion = GitVersion_AssemblySemFileVer; 

							println("PUSH VERSION TO RELEASE SERVER : [${setupVersion}]");
							version = new com.lectra.ci.release.Version(solutionFile,"pilot-all",setupVersion)
							version.publishVersion(steps)

							println "Publish to	:					${binRepository}"
							println "Solution   :					${jobnameparts[jobnameparts.length-2]}"
							println "Version	:					${setupVersion}"

							bat """ xcopy distrib\\release ${binRepository}-${setupVersion.replaceAll('\\.','_')}\\distrib\\release /Y /S /F /I """
							bat """ IF EXIST distrib\\conf\\latest.remote/nul ( del /Q distrib\\conf\\latest.remote ) """
							bat """ xcopy distrib\\conf ${binRepository}-${setupVersion.replaceAll('\\.','_')}\\distrib\\conf /Y /S /F /I """

							currentBuild.displayName = currentBuild.displayName + "+Setup"
					
				}
			}
		}
	}

	post {
		cleanup {
			cleanWs notFailBuild: true
		}
	}
}
