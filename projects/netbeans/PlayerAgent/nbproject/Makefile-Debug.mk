#
# Generated Makefile - do not edit!
#
# Edit the Makefile in the project folder instead (../Makefile). Each target
# has a -pre and a -post target defined where you can add customized code.
#
# This makefile implements configuration specific macros and targets.


# Environment
MKDIR=mkdir
CP=cp
CCADMIN=CCadmin
RANLIB=ranlib
CC=gcc
CCC=g++
CXX=g++
FC=
AS=as

# Macros
CND_PLATFORM=GNU-Linux-x86
CND_CONF=Debug
CND_DISTDIR=dist

# Include project Makefile
include Makefile

# Object Directory
OBJECTDIR=build/${CND_CONF}/${CND_PLATFORM}

# Object Files
OBJECTFILES= \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/PlayerAgent/PlayerAgentHandler.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/PlayerAgent/PlayerAgent.o

# C Compiler Flags
CFLAGS=

# CC Compiler Flags
CCFLAGS=-lpthread
CXXFLAGS=-lpthread

# Fortran Compiler Flags
FFLAGS=

# Assembler Flags
ASFLAGS=

# Link Libraries and Options
LDLIBSOPTIONS=../Common/../../../build/${CND_PLATFORM}/${CND_CONF}/libcommon.a -lmysqlclient

# Build Targets
.build-conf: ${BUILD_SUBPROJECTS}
	${MAKE}  -f nbproject/Makefile-Debug.mk ../../../build/GNU-Linux-x86/Debug/PlayerAgent

../../../build/GNU-Linux-x86/Debug/PlayerAgent: ../Common/../../../build/${CND_PLATFORM}/${CND_CONF}/libcommon.a

../../../build/GNU-Linux-x86/Debug/PlayerAgent: ${OBJECTFILES}
	${MKDIR} -p ../../../build/GNU-Linux-x86/Debug
	${LINK.cc} -o ../../../build/${CND_PLATFORM}/${CND_CONF}/PlayerAgent ${OBJECTFILES} ${LDLIBSOPTIONS} 

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/PlayerAgent/PlayerAgentHandler.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/PlayerAgent/PlayerAgentHandler.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/PlayerAgent
	${RM} $@.d
	$(COMPILE.cc) -g -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/PlayerAgent/PlayerAgentHandler.o ../../../src/PlayerAgent/PlayerAgentHandler.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/PlayerAgent/PlayerAgent.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/PlayerAgent/PlayerAgent.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/PlayerAgent
	${RM} $@.d
	$(COMPILE.cc) -g -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/PlayerAgent/PlayerAgent.o ../../../src/PlayerAgent/PlayerAgent.cpp

# Subprojects
.build-subprojects:
	cd ../Common && ${MAKE}  -f Makefile CONF=Debug

# Clean Targets
.clean-conf: ${CLEAN_SUBPROJECTS}
	${RM} -r build/Debug
	${RM} ../../../build/GNU-Linux-x86/Debug/PlayerAgent

# Subprojects
.clean-subprojects:
	cd ../Common && ${MAKE}  -f Makefile CONF=Debug clean

# Enable dependency checking
.dep.inc: .depcheck-impl

include .dep.inc
