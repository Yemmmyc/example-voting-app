# EKS Cluster provisioned in the discovered Default VPC
resource "aws_eks_cluster" "voting_app" {
  name     = var.cluster_name
  version  = var.cluster_version
  role_arn = aws_iam_role.cluster.arn

  vpc_config {
    subnet_ids              = local.suitable_subnet_ids
    endpoint_public_access  = true
    endpoint_private_access = true
  }

  lifecycle {
    precondition {
      condition     = length(local.distinct_azs) >= 2
      error_message = "EKS requires at least two subnets in distinct Availability Zones."
    }
  }

  depends_on = [
    aws_iam_role_policy_attachment.cluster_AmazonEKSClusterPolicy,
    aws_iam_role_policy_attachment.cluster_AmazonEKSVPCResourceController
  ]
}

# EKS Managed Node Group attached to Default VPC subnets
resource "aws_eks_node_group" "voting_app_nodes" {
  cluster_name    = aws_eks_cluster.voting_app.name
  node_group_name = "${var.cluster_name}-node-group"
  node_role_arn   = aws_iam_role.node_group.arn
  subnet_ids      = local.suitable_subnet_ids

  instance_types = var.node_instance_types

  scaling_config {
    desired_size = var.desired_node_count
    max_size     = var.max_node_count
    min_size     = var.min_node_count
  }

  update_config {
    max_unavailable = 1
  }

  depends_on = [
    aws_iam_role_policy_attachment.node_AmazonEKSWorkerNodePolicy,
    aws_iam_role_policy_attachment.node_AmazonEKS_CNI_Policy,
    aws_iam_role_policy_attachment.node_AmazonEC2ContainerRegistryReadOnly
  ]
}
