provider "aws" {
  region = var.aws_region

  default_tags {
    tags = {
      Project     = "VotingApp-Capstone"
      ManagedBy   = "Terraform"
      Environment = var.environment
    }
  }
}
