## Project
Miller - Program to convert .STL to .nc files which would be used by CNC machine to mill accodring to the .stl
## General Rules
- If opening any claude.md file in this project - full load into the context window is Mandatory;
- Apply engineering mindset focused on precision, calculation and guessing avoidance where it possible;
- Avoid emojis and symbols arrows etc;
- Provide unbiased answers without blind supporting of mine ideas;
- Avoid asking clarification questions, do it only in absolute blocker cases;
- Keep answers explicit and short using simple language;
- When in request present question tag '?' it means question, answer it instead of starting work;
- Project is developed in insulated "cluster" architecture, each cluster (in case of issue of one cluster, error don't propagate to the different cluster and server continuing work) is insulated between each other and data between clusters is forwarded by special "gates". each new functionality should be assigned to the separate cluster.

## Development phases setupped in hooks
 1. "Task definition" - read only -  split user request to "separate task" quantity which will allow cover all requests. Make pre-research in project files to understand meaning of the separate tasks. next for each separate task think about 15 potential quantitative and qualitative acceptance criteria, next select from 2 up to 8 "meaningful acceptance criteria" for each separate task, which mitigating risks of wrong implementation. Print "KPI" table in chat, add there "separate tasks" and "meaningful acceptance criteria".
 2. "Files pre-research" - read only - skip for trivial tasks like PR creation or questions. Research project files and identify for each "meaningful acceptance criteria" from "KPI" table, it's "initial state" and summary of related "code architecture". Next print "KPI" table again with newly added parameters.
 3. "WEB research" - read only - skip for trivial tasks like PR creation. Rephrase in generic way tasks from "task definitions" and "meaningful acceptance criteria". Next check in WEB how such tasks was solved, target to have 3 different solutions per each "separate task". Next think about pros and cons for each solution, and summary how to implement each solution. Next evaluate which solution fits the best for projects specific; Links provided in WEB are forbiden to be pasted in project due to cyber security reasons, only stricting to approach of researched solutions is allowed. 
 4. "Planning" - read only - skip for trivial tasks like PR creation or answering question. Using all available inputs from previous phases, evaluate if all needed inputs are collected, if no then continue research. for each "meaningful acceptance criteria" from "KPI" table define risks during the implementation and it's mitigation way, next print "KPI" table again with newly added parameters; define code modification architecture, decompose it to sub tasks. Next prepare structured plan for work execution to achieve acceptance criteria and fixing identified existing or preventing potential bugs. Asking questions not allowed, exception only in case of missing blocking information (which prevents in task execution at all).
 5. "Execution" - execute tasks identified in "Planning" phase. In case of missing of any information continue research. 
 6. "Testing" - think about all possible exceptions/errors done based on "meaningful acceptance criteria": unintentionally based on defined acceptance criteria during this modification; unintentional feature functionality change by developer during future feature functionality extension; by end user during feature usage; intentionally by hacker during attacks. Next think about reflecting in tests current expected proper behavior of tests (which will allow detection of unintentional behavior change). Create new test cases if any of functionality is not covered. Execute all tests even if some parts of project wasn't modified; If any test case failed, add "separate task" about code fix (target to avoid "separate tasks" about existing tests modification, because before it passed the tests) into the "KPI" table, next print "KPI" table again with newly added parameters, and return to phase "task definition". In case of blocking situations where impossible to avoid existing tests modification, justify test modification reason to user and ask for test modification approval, only after direct approval test might be modified.
 7. "Documentation" - only if "intended root claude.md rules violation" or "fixing bugs which existing before session start" are happened, add shortest possible notes about it into the local claude.md in the folders where was done modification; In case of noticing that user clarified initial request during the session add one raw in root "vocabulary.md" with proper interpretation of initial request;
 8. "Reporting" - create report which will contain 1 raw summary for each development phase; next to the each "meaningful acceptance criteria" evaluate "post execution state" and add all of it to the "KPI" table. print full "KPI" table.
 9. "post execution" - only for answering questions

## Development Rules
- app should be buildable on linux without any code modification/adjustment
- phases 1, 4,5,6,7,8,9 should run one buy one, phases 2 and 3 running simultaneously, phase 4 starts when both phases 2 and 3 are finished 
- Build all components after every change.
- Increment version patch number (X in `Build_Y.Z.X`) for any app element change
- after each functionality creation create a new commit (from commit should be excluded builded components)
- guidance from code comments should be treated as higher authority in this particular function where it is written than claude.md rules

## Code Rules
- **project Claude.md in root and .claude might be updated only by user, others folder level claude.md might be updated by claude and should contain only lessons learned and intended rules deviation** 
- **No fallbacks.** No fallback methods, hidden switches or dual code paths. If an existing fallback is found during modification, flag it to user explicitly
- **maximally reuse already existing functions instead of creation new ones** 
- **Use variables** instead of static values for future reuse, as well for frontend colors which declaration is located in one css file
- **Comments only when they add value** -- do not add decorative or obvious comments