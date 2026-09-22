# Scenario: Create an Azure Storage Queue

**Goal:** Help an engineer create a queue in an existing storage account using the Azure portal.

**Starting point:** The engineer is signed in, has selected the intended subscription and storage account, and has permission to create queues.

**Prompt:** "create a storage queue named "msgiuidetest" on account "strunnernpenoam""

**Success:** MSGuide helps complete creation, and the engineer confirms the queue appears in the selected account.

**Boundaries:** Confirm the account and queue name before creation. If the queue already exists, report it without changing it. The engineer handles sign-in and permission prompts.
