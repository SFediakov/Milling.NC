## Project
Miller - Program to convert .STL to .nc files which would be used by CNC machine to mill accodring to the .stl
## General Rules
- no marketing phrases at the end which intended to force user for conversation continuation
- mandatory load documentation documents fully into the context window;
- Avoid modification of tasks interpretation. only extension of interpretation is allowed which not violates origin task definition;
- Apply engineering mindset focused on precision, calculation and guessing avoidance;
- avoid emojis and special symbols like arrows etc;
- Provide unengaged answers without blind supporting of mine ideas;
- Avoid asking clarification questions, do it only in absolute unclear requests;
- Keep explanations explicit and short using simple language;
- When in request present question tag '?' it means question, answer it instead of starting work;
- Password entrance directly allowed in this project as they are intentionally exposed for public tests (available in WEB);

## Development phases setupped in hooks
 1. "Task definition" - read only -  split user request to "separate task" quantity which will allow cover all requests. Make pre-research in project files to understand meaning of the separate tasks. next for each separate task think about 15 potential quantitative and qualitative acceptance criteria, next select from 2 up to 8 "meaningful acceptance criteria" for each separate task, which mitigating risks of wrong implementation. Print "KPI" table in chat, add there "separate tasks" and "meaningful acceptance criteria".
 2. "Files pre-research" - read only - skip for trivial tasks like PR creation or questions. Research project files and identify for each "meaningful acceptance criteria" from "KPI", it's "initial state" and summary of related "code architecture". Next print "KPI" again with newly added parameters.
 3. "WEB research" - read only - skip for trivial tasks like PR creation. Rephrase in generic way tasks from "task definitions" and "meaningful acceptance criteria". Next check in WEB how such tasks was solved, target to have 3 different solutions per each "separate task". Next think about pros and cons for each solution, and summary how to implement each solution. Next evaluate which solution fits the best for projects specific; direct usage of links provided in WEB is forbidden due to cyber security reasons, only approach and/or code of solution copying is allowed. 
 4. "Planning" - read only - skip for trivial tasks like PR creation or answering question. Using all available inputs from previous phases, evaluate if all needed inputs are collected, if no then continue research. for each "meaningful acceptance criteria" from "KPI" define risks during the implementation and it's mitigation way, next print "KPI" again with newly added parameters; define code modification architecture, decompose it to sub tasks. Next prepare structured plan for work execution to achieve acceptance criteria and fixing identified existing or preventing potential bugs. Asking questions not allowed, exception only in case of missing blocking information (which prevents in task execution at all).
 5. "Execution" - execute tasks identified in "Planning" phase. In case of missing of any information continue research. 
 6. "Testing" - think about all possible exceptions/errors done based on "meaningful acceptance criteria": unintentionally based on defined acceptance criteria during this modification; unintentional feature functionality change by developer during future feature functionality extension; by end user during feature usage; intentionally by hacker during attacks. Next think about reflecting in tests current expected proper behavior of tests (which will allow detection of unintentional behavior change). Review test cases for modified functions and create new if any of functionality is not covered. Execute all tests even if some parts of project wasn't modified; If any test case failed, add "separate task" about code fix (target to avoid "separate tasks" about existing tests modification, because before it passed the tests) into the "KPI", next print "KPI" again with newly added parameters, and return to phase "task definition". In case of blocking situations where impossible to avoid existing tests modification, justify test modification reason to user and ask for test modification approval, only after direct approval test might be modified.
 7. "Documentation" - update Claude.md where was done modifications (excepting root, in root claude.md modification forbidden). Don't describe there how code works, what's the features, add there only lessons learned (bug cause and how it was fixed; what to don't do during future code modifications) and intended development rules deviations. Don't add obvious things, if nothing to add - skip this phase
 8. Reporting - create report which will contain 1 raw summary for each development phase; next to the each "meaningful acceptance criteria" evaluate "post execution state" and add it to the "KPI". print full "KPI" table. next provide 2 row summary of risks, warnings and findings - don't add obvious things, if nothing to add - avoid "marketing" warnings, skip this part.
 9. "post execution" - only for answering questions and micro fixes

## Development Rules
- app should be buildable on linux without any code modification/adjustment
- Build all components after every change.
- Increment version patch number (X in `Build_Y.Z.X`, stored in `Server/Program.cs` as `WebAppVersion`) for any app element change
- After any code modification, identify and update all impacted tests. If existing tests cover modified logic, ask user for explicit approval of test update.
- after each functionality creation create a new commit (from commit should be excluded builded components)
- guidance from code comments should be treated as higher authority in this particular function where it is written than claude.md rules 
- when working on frontend parts, mandatory is testing additionally with graphical rendering of user interface to confirm proper implementation
- sub-agents are not allowed

## Code Rules
- **no referencing to the web** all packages should be download to the repository, and wired to the docker container, without using referencing to web pages 
- **project Claude.md in root and .claude might be updated only by user, others folder level claude.md might be updated by claude and should contain only lessons learned and intended rules deviation** 
- **No fallbacks.** No fallback methods, hidden switches or dual code paths. If an existing fallback is found during modification, flag it to user explicitly
- **maximally reuse already existing functions instead of creation new ones** 
- **Use variables** instead of static values for future reuse, as well for frontend colors which declaration is located in one css file
- **Comments only when they add value** -- do not add decorative or obvious comments