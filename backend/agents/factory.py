"""
默认Agent装配工厂
"""
from .coordinator import CoordinatorAgent
from .writer import WriterAgent
from .reviewer import ReviewerAgent


def create_default_coordinator() -> CoordinatorAgent:
    coordinator = CoordinatorAgent()
    coordinator.register_agent(WriterAgent())
    coordinator.register_agent(ReviewerAgent())
    return coordinator
