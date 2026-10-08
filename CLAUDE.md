# CLAUDE.md

<!-- 이 프로젝트에만 해당하는 내용만 적는다. 코딩 지침, CHANGELOG 규칙, 멀티 에이전트 규칙, 스킬은 구글 드라이브 AgentSync의 전체 메모리에서 공통으로 불러온다. -->
<!-- 0_Startup을 복사해 새 프로젝트를 만들면 아래 칸을 채우고, 쓸 내용이 없는 칸은 지운다. -->

## 프로젝트 개요
<!-- 전시명, 설치 장소, 씬 흐름 -->
- 전시명: 전화부스
- 설치 장소: 천안 독립기념관
- 0_Startup 템플릿을 복사한 직후 상태이며, 전화부스 콘텐츠는 아직 구현 전이다.
- 씬: `0_Idle` 하나뿐이다(빌드 첫 씬, Main Camera·EventSystem만 있음).

## 하드웨어·외부 연동
<!-- 센서, RFID, 시리얼, API 엔드포인트 -->

## 공통 규칙의 예외
<!-- 예: 씬 매니저는 GameManagerBase 대신 MonoBehaviour 유지 -->
- 스킬 `unity-stack-scaffold`, `unity-network-protocol`, `changelog`는 `C:\Users\HULIAC\Unity\claude-skills` 원본을 `.claude/skills/`에 복사해 이 프로젝트에서만 쓴다. 복사본이라 원본을 고치면 다시 복사해야 반영된다.
