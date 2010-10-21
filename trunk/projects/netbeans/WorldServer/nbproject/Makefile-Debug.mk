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
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/CombatHandler.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/WorldCharAgentProjectHandler.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/WorldAgentHandler.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/CommandHandler.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/AbilityHandler.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/WorldServer.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/WorldCharAgentHandler.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/NPCHandler.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/CharacterHandler.o

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
	${MAKE}  -f nbproject/Makefile-Debug.mk ../../../build/GNU-Linux-x86/Debug/WorldServer

../../../build/GNU-Linux-x86/Debug/WorldServer: ../Common/../../../build/${CND_PLATFORM}/${CND_CONF}/libcommon.a

../../../build/GNU-Linux-x86/Debug/WorldServer: ${OBJECTFILES}
	${MKDIR} -p ../../../build/GNU-Linux-x86/Debug
	${LINK.cc} -o ../../../build/${CND_PLATFORM}/${CND_CONF}/WorldServer ${OBJECTFILES} ${LDLIBSOPTIONS} 

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/CombatHandler.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/WorldServer/CombatHandler.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer
	${RM} $@.d
	$(COMPILE.cc) -g -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/CombatHandler.o ../../../src/WorldServer/CombatHandler.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/WorldCharAgentProjectHandler.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/WorldServer/WorldCharAgentProjectHandler.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer
	${RM} $@.d
	$(COMPILE.cc) -g -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/WorldCharAgentProjectHandler.o ../../../src/WorldServer/WorldCharAgentProjectHandler.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/WorldAgentHandler.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/WorldServer/WorldAgentHandler.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer
	${RM} $@.d
	$(COMPILE.cc) -g -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/WorldAgentHandler.o ../../../src/WorldServer/WorldAgentHandler.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/CommandHandler.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/WorldServer/CommandHandler.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer
	${RM} $@.d
	$(COMPILE.cc) -g -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/CommandHandler.o ../../../src/WorldServer/CommandHandler.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/AbilityHandler.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/WorldServer/AbilityHandler.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer
	${RM} $@.d
	$(COMPILE.cc) -g -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/AbilityHandler.o ../../../src/WorldServer/AbilityHandler.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/WorldServer.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/WorldServer/WorldServer.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer
	${RM} $@.d
	$(COMPILE.cc) -g -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/WorldServer.o ../../../src/WorldServer/WorldServer.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/WorldCharAgentHandler.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/WorldServer/WorldCharAgentHandler.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer
	${RM} $@.d
	$(COMPILE.cc) -g -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/WorldCharAgentHandler.o ../../../src/WorldServer/WorldCharAgentHandler.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/NPCHandler.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/WorldServer/NPCHandler.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer
	${RM} $@.d
	$(COMPILE.cc) -g -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/NPCHandler.o ../../../src/WorldServer/NPCHandler.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/CharacterHandler.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/WorldServer/CharacterHandler.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer
	${RM} $@.d
	$(COMPILE.cc) -g -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/WorldServer/CharacterHandler.o ../../../src/WorldServer/CharacterHandler.cpp

# Subprojects
.build-subprojects:
	cd ../Common && ${MAKE}  -f Makefile CONF=Debug
	cd ../Common && ${MAKE}  -f Makefile CONF=Debug

# Clean Targets
.clean-conf: ${CLEAN_SUBPROJECTS}
	${RM} -r build/Debug
	${RM} ../../../build/GNU-Linux-x86/Debug/WorldServer

# Subprojects
.clean-subprojects:
	cd ../Common && ${MAKE}  -f Makefile CONF=Debug clean
	cd ../Common && ${MAKE}  -f Makefile CONF=Debug clean

# Enable dependency checking
.dep.inc: .depcheck-impl

include .dep.inc
